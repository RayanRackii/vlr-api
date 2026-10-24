using Platform.Api.Services.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Platform-admin tenant edit while the JWT still carries a different tenant id.
/// The ambient global query filter must not hide the target tenant's modules,
/// and TaxId/Subdomain uniqueness must ignore the row being updated.
/// </summary>
public sealed class AdminTenantUpdateConflictTests
{
    private const string ConflictMessage =
        "A tenant with the same TaxId or Subdomain already exists.";

    [Fact]
    public async Task Update_same_tax_id_and_subdomain_succeeds_when_ambient_tenant_differs()
    {
        await using var harness = await Harness.CreateAsync();
        var (target, other) = await harness.CreatePairAsync();
        harness.TenantProvider.TenantId = other.Id;

        var controller = new AdminTenantsController(harness.Service);
        var action = await controller.Update(
            target.Id,
            UpdateRequest(target),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var updated = Assert.IsType<TenantAdminResponseDto>(ok.Value);
        Assert.Equal(target.TaxId, updated.TaxId);
        Assert.Equal(target.Subdomain, updated.Subdomain);
        Assert.Equal(
            new[] { PlatformModules.Catalog, PlatformModules.Rentals },
            updated.ActiveModules.Select(m => m.ModuleName).OrderBy(name => name).ToArray());

        Assert.Equal(2, await harness.CountModulesAsync(target.Id));
        Assert.Equal(1, await harness.CountFamiliesAsync(target.Id));
        Assert.Equal(1, await harness.CountModulesAsync(other.Id));
        Assert.Equal([PlatformModules.Inventory], await harness.ModuleNamesAsync(other.Id));
    }

    [Fact]
    public async Task Update_to_another_tenants_tax_id_returns_409()
    {
        await using var harness = await Harness.CreateAsync();
        var (target, other) = await harness.CreatePairAsync();
        harness.TenantProvider.TenantId = other.Id;

        var action = await new AdminTenantsController(harness.Service).Update(
            target.Id,
            UpdateRequest(target, taxId: other.TaxId),
            CancellationToken.None);

        AssertConflict(action);
        Assert.Equal(target.TaxId, await harness.TaxIdAsync(target.Id));
        Assert.Equal(2, await harness.CountModulesAsync(target.Id));
    }

    [Fact]
    public async Task Update_to_another_tenants_subdomain_returns_409()
    {
        await using var harness = await Harness.CreateAsync();
        var (target, other) = await harness.CreatePairAsync();
        harness.TenantProvider.TenantId = other.Id;

        var action = await new AdminTenantsController(harness.Service).Update(
            target.Id,
            UpdateRequest(target, subdomain: other.Subdomain),
            CancellationToken.None);

        AssertConflict(action);
        Assert.Equal(target.Subdomain, await harness.SubdomainAsync(target.Id));
        Assert.Equal(2, await harness.CountModulesAsync(target.Id));
    }

    [Fact]
    public async Task List_and_get_return_modules_saved_at_creation_when_ambient_tenant_differs()
    {
        await using var harness = await Harness.CreateAsync();
        harness.TenantProvider.TenantId = Guid.NewGuid();

        var created = await harness.Service.CreateAsync(
            CreateRequest("ficc", [PlatformModules.Rentals, PlatformModules.Catalog]),
            CancellationToken.None);

        Assert.Equal(
            new[] { PlatformModules.Catalog, PlatformModules.Rentals },
            created.ActiveModules.Select(m => m.ModuleName).OrderBy(name => name).ToArray());

        var listed = await harness.Service.ListAsync(CancellationToken.None);
        var fromList = Assert.Single(listed, tenant => tenant.Id == created.Id);
        Assert.Equal(
            created.ActiveModules.Select(m => m.ModuleName).OrderBy(name => name),
            fromList.ActiveModules.Select(m => m.ModuleName).OrderBy(name => name));

        var loaded = await harness.Service.GetByIdAsync(created.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(
            created.ActiveModules.Select(m => m.ModuleName).OrderBy(name => name),
            loaded.ActiveModules.Select(m => m.ModuleName).OrderBy(name => name));

        Assert.Equal(2, await harness.CountModulesAsync(created.Id));
    }

    private static void AssertConflict(ActionResult<TenantAdminResponseDto> action)
    {
        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal(
            ConflictMessage,
            conflict.Value?.GetType().GetProperty("error")?.GetValue(conflict.Value));
    }

    private static CreateTenantRequestDto CreateRequest(
        string slug,
        IReadOnlyList<string> modules)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new CreateTenantRequestDto
        {
            LegalName = slug,
            TaxId = $"99{suffix}000191",
            Subdomain = $"{slug}-{suffix}",
            ActiveModules = modules,
            AssetFamilyKeys = [AssetFamilyKeys.Generic],
        };
    }

    private static UpdateTenantRequestDto UpdateRequest(
        TenantAdminResponseDto tenant,
        string? taxId = null,
        string? subdomain = null) =>
        new()
        {
            LegalName = tenant.LegalName,
            TaxId = taxId ?? tenant.TaxId,
            Subdomain = subdomain ?? tenant.Subdomain ?? throw new InvalidOperationException("Subdomain is required."),
            ActiveModules = [PlatformModules.Rentals, PlatformModules.Catalog],
            AssetFamilyKeys = [AssetFamilyKeys.Generic],
        };

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(AppDbContext db, FakeTenantProvider tenantProvider, AdminTenantService service)
        {
            Db = db;
            TenantProvider = tenantProvider;
            Service = service;
        }

        public AppDbContext Db { get; }

        public FakeTenantProvider TenantProvider { get; }

        public AdminTenantService Service { get; }

        public static async Task<Harness> CreateAsync()
        {
            var tenantProvider = new FakeTenantProvider { TenantId = null };
            var db = InMemoryAppDb.Create(tenantProvider);
            db.AssetFamilies.Add(new AssetFamily
            {
                Key = AssetFamilyKeys.Generic,
                Label = "Genérico",
                FieldSchemaJson = """{"fields":[]}""",
                SortOrder = 4,
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var service = new AdminTenantService(
                db,
                new FakeTenantUserAdminService(),
                new FakePlatformAdminMembershipService(),
                new FakeTenantAccessBootstrapper(),
                NullLogger<AdminTenantService>.Instance,
                new TenantProvisioner(db, NullLogger<TenantProvisioner>.Instance),
                new TenantPurger(db, new FakeSupabaseAuthAdminClient(), Options.Create(new PlatformAdminOptions()), NullLogger<TenantPurger>.Instance, Options.Create(new StorageOptions()), new RecordingStorageProvider()));

            return new Harness(db, tenantProvider, service);
        }

        public async Task<(TenantAdminResponseDto Target, TenantAdminResponseDto Other)> CreatePairAsync()
        {
            var previous = TenantProvider.TenantId;
            TenantProvider.TenantId = null;

            var target = await Service.CreateAsync(
                CreateRequest("ficc", [PlatformModules.Rentals, PlatformModules.Catalog]),
                CancellationToken.None);
            var other = await Service.CreateAsync(
                CreateRequest("other", [PlatformModules.Inventory]),
                CancellationToken.None);

            TenantProvider.TenantId = previous;
            return (target, other);
        }

        public Task<int> CountModulesAsync(Guid tenantId) =>
            Db.TenantModules
                .IgnoreQueryFilters()
                .CountAsync(module => module.TenantId == tenantId);

        public async Task<string[]> ModuleNamesAsync(Guid tenantId) =>
            await Db.TenantModules
                .IgnoreQueryFilters()
                .Where(module => module.TenantId == tenantId)
                .OrderBy(module => module.ModuleName)
                .Select(module => module.ModuleName)
                .ToArrayAsync();

        public Task<int> CountFamiliesAsync(Guid tenantId) =>
            Db.TenantAssetFamilies
                .IgnoreQueryFilters()
                .CountAsync(row => row.TenantId == tenantId);

        public async Task<string> TaxIdAsync(Guid tenantId) =>
            await Db.Tenants
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => tenant.TaxId)
                .SingleAsync();

        public async Task<string?> SubdomainAsync(Guid tenantId) =>
            await Db.Tenants
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => tenant.Subdomain)
                .SingleAsync();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
