using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Services.Tenancy;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// ADR 0010: resource-planning no longer declares RequiredModules, so rentals/catalog/resource-planning
/// can be sold in any combination, and the old static guarantees are business guards on disable.
/// Guard cases run against a second tenant to prove the guards are tenant-scoped.
/// </summary>
public sealed class AdminTenantModuleDisableGuardTests
{
    private static readonly string[] AllThree =
        [PlatformModules.Catalog, PlatformModules.Rentals, PlatformModules.ResourcePlanning];

    [Theory]
    [InlineData("resource-planning")]
    [InlineData("inventory,resource-planning")]
    [InlineData("catalog,resource-planning")]
    [InlineData("rentals,resource-planning")]
    [InlineData("os,resource-planning")]
    public async Task Resource_planning_activates_without_catalog_or_rentals(string moduleList)
    {
        var modules = moduleList.Split(',');
        await using var harness = await Harness.CreateAsync();

        var created = await harness.CreateTenantAsync("rp", modules);

        Assert.Contains(PlatformModules.ResourcePlanning, await harness.ActiveModuleNamesAsync(created.Id));
        // The Asset Registry capability is provisioned for every tenant (default family).
        Assert.Equal(1, await harness.CountFamiliesAsync(created.Id));
    }

    [Fact]
    public void Resource_planning_requires_only_the_asset_registry_capability()
    {
        var descriptor = Assert.Single(
            PlatformModuleCatalog.All,
            m => m.Key == PlatformModules.ResourcePlanning);

        Assert.Empty(descriptor.RequiredModules);
        Assert.Equal([PlatformModuleCatalog.AssetRegistryCapability], descriptor.RequiredCapabilities);
        Assert.Empty(descriptor.Provides);
    }

    [Fact]
    public async Task Disabling_resource_planning_is_rejected_while_allocations_are_unreleased()
    {
        await using var harness = await Harness.CreateAsync();
        var (a, b) = await harness.CreatePairAsync();
        await harness.SeedAllocationAsync(a.Id, AllocationStatus.Running);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.UpdateModulesAsync(a, [PlatformModules.Catalog, PlatformModules.Rentals]));

        Assert.Contains("Cannot disable resource-planning: 1 unreleased", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PlatformModules.ResourcePlanning, await harness.ActiveModuleNamesAsync(a.Id));

        // Tenant B has no allocations: A's rows must not block B, and B's disable still works.
        await harness.UpdateModulesAsync(b, [PlatformModules.Catalog, PlatformModules.Rentals]);
        Assert.DoesNotContain(PlatformModules.ResourcePlanning, await harness.ActiveModuleNamesAsync(b.Id));
    }

    [Theory]
    [InlineData(AllocationStatus.Released)]
    [InlineData(AllocationStatus.Cancelled)]
    public async Task Terminal_allocations_do_not_block_disabling(AllocationStatus status)
    {
        await using var harness = await Harness.CreateAsync();
        var (a, _) = await harness.CreatePairAsync();
        await harness.SeedAllocationAsync(a.Id, status);

        await harness.UpdateModulesAsync(a, [PlatformModules.Catalog]);

        var active = await harness.ActiveModuleNamesAsync(a.Id);
        Assert.DoesNotContain(PlatformModules.ResourcePlanning, active);
        Assert.DoesNotContain(PlatformModules.Rentals, active);
    }

    [Theory]
    [InlineData(PlatformModules.Rentals)]
    [InlineData(PlatformModules.Catalog)]
    public async Task Disabling_a_dependency_is_rejected_while_resource_planning_stays_and_allocations_are_unreleased(
        string disabled)
    {
        await using var harness = await Harness.CreateAsync();
        var (a, b) = await harness.CreatePairAsync();
        await harness.SeedAllocationAsync(a.Id, AllocationStatus.Scheduled);
        var remaining = AllThree.Where(m => m != disabled).ToArray();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.UpdateModulesAsync(a, remaining));

        Assert.Contains($"Cannot disable {disabled}: 1 unreleased", ex.Message, StringComparison.Ordinal);
        Assert.Contains(disabled, await harness.ActiveModuleNamesAsync(a.Id));

        await harness.UpdateModulesAsync(b, remaining);
        Assert.DoesNotContain(disabled, await harness.ActiveModuleNamesAsync(b.Id));
    }

    [Theory]
    [InlineData(PlatformModules.Rentals)]
    [InlineData(PlatformModules.Catalog)]
    public async Task Disabling_a_dependency_is_allowed_when_only_another_tenant_has_unreleased_allocations(
        string disabled)
    {
        await using var harness = await Harness.CreateAsync();
        var (a, b) = await harness.CreatePairAsync();
        await harness.SeedAllocationAsync(b.Id, AllocationStatus.Cleaning);

        await harness.UpdateModulesAsync(a, AllThree.Where(m => m != disabled).ToArray());

        var active = await harness.ActiveModuleNamesAsync(a.Id);
        Assert.DoesNotContain(disabled, active);
        Assert.Contains(PlatformModules.ResourcePlanning, active);
    }

    [Fact]
    public async Task Disabling_os_is_allowed_with_unreleased_allocations()
    {
        await using var harness = await Harness.CreateAsync();
        var created = await harness.CreateTenantAsync("os", [.. AllThree, PlatformModules.WorkOrders]);
        await harness.SeedAllocationAsync(created.Id, AllocationStatus.Running);

        await harness.UpdateModulesAsync(created, AllThree);

        var active = await harness.ActiveModuleNamesAsync(created.Id);
        Assert.DoesNotContain(PlatformModules.WorkOrders, active);
        Assert.Contains(PlatformModules.ResourcePlanning, active);
    }

    [Fact]
    public async Task Guard_sees_the_target_tenant_even_when_the_ambient_tenant_differs()
    {
        await using var harness = await Harness.CreateAsync();
        var (a, b) = await harness.CreatePairAsync();
        await harness.SeedAllocationAsync(a.Id, AllocationStatus.Running);
        harness.TenantProvider.TenantId = b.Id;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.UpdateModulesAsync(a, [PlatformModules.Catalog, PlatformModules.Rentals]));
    }

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
                Label = "Generico",
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

        public Task<TenantAdminResponseDto> CreateTenantAsync(string slug, IReadOnlyList<string> modules)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            return Service.CreateAsync(
                new CreateTenantRequestDto
                {
                    LegalName = slug,
                    TaxId = $"99{suffix}000191",
                    Subdomain = $"{slug}-{suffix}",
                    ActiveModules = modules,
                    AssetFamilyKeys = [AssetFamilyKeys.Generic],
                },
                CancellationToken.None);
        }

        public async Task<(TenantAdminResponseDto A, TenantAdminResponseDto B)> CreatePairAsync()
        {
            var a = await CreateTenantAsync("tenant-a", AllThree);
            var b = await CreateTenantAsync("tenant-b", AllThree);
            return (a, b);
        }

        public async Task SeedAllocationAsync(Guid tenantId, AllocationStatus status)
        {
            var previous = TenantProvider.TenantId;
            TenantProvider.TenantId = null;
            Db.CatalogOrderItemResourceAllocations.Add(new CatalogOrderItemResourceAllocation
            {
                TenantId = tenantId,
                RequirementId = Guid.NewGuid(),
                ReservationItemId = Guid.NewGuid(),
                Sequence = 1,
                PlanningRevision = 1,
                AllocatedQuantity = 1,
                RentalAssetId = Guid.NewGuid(),
                PlannedWorkStart = DateTimeOffset.UtcNow.AddDays(1),
                PlannedWorkEnd = DateTimeOffset.UtcNow.AddDays(1).AddHours(1),
                TimeZoneId = "America/Sao_Paulo",
                Status = status,
            });
            await Db.SaveChangesAsync();
            TenantProvider.TenantId = previous;
        }

        public Task<TenantAdminResponseDto> UpdateModulesAsync(
            TenantAdminResponseDto tenant,
            IReadOnlyList<string> modules) =>
            Service.UpdateAsync(
                tenant.Id,
                new UpdateTenantRequestDto
                {
                    LegalName = tenant.LegalName,
                    TaxId = tenant.TaxId,
                    Subdomain = tenant.Subdomain ?? throw new InvalidOperationException("Subdomain is required."),
                    ActiveModules = modules,
                    AssetFamilyKeys = [AssetFamilyKeys.Generic],
                },
                CancellationToken.None);

        public async Task<string[]> ActiveModuleNamesAsync(Guid tenantId) =>
            await Db.TenantModules
                .IgnoreQueryFilters()
                .Where(module => module.TenantId == tenantId && module.IsActive)
                .OrderBy(module => module.ModuleName)
                .Select(module => module.ModuleName)
                .ToArrayAsync();

        public Task<int> CountFamiliesAsync(Guid tenantId) =>
            Db.TenantAssetFamilies
                .IgnoreQueryFilters()
                .CountAsync(row => row.TenantId == tenantId);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
