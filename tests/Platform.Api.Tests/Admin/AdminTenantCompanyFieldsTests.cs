using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Services.Tenancy;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Platform-admin edit of trade name, contact and address on the TARGET tenant. The ambient tenant
/// is a different company, and the other tenant must never change.
/// </summary>
public sealed class AdminTenantCompanyFieldsTests
{
    private static readonly TenantAdminAddressDto SampleAddress = new(
        "01310-100", "Av. Paulista", "1000", "Sala 5", "Bela Vista", "Sao Paulo", "sp");

    [Fact]
    public async Task Update_writes_company_fields_on_target_only_and_get_returns_them()
    {
        await using var harness = await Harness.CreateAsync();
        var (target, other) = await harness.CreatePairAsync();
        harness.TenantProvider.TenantId = other.Id;

        var action = await new AdminTenantsController(harness.Service).Update(
            target.Id,
            UpdateRequest(target, request => request with
            {
                TradeName = "  Quadra Norte  ",
                ContactEmail = "contato@quadra.com.br",
                ContactPhone = "(11) 98888-7777",
                Address = SampleAddress,
            }),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var updated = Assert.IsType<TenantAdminResponseDto>(ok.Value);
        Assert.Equal("Quadra Norte", updated.TradeName);
        Assert.Equal("contato@quadra.com.br", updated.ContactEmail);
        Assert.False(string.IsNullOrWhiteSpace(updated.ContactPhone));
        Assert.Equal("01310100", updated.Address.PostalCode);
        Assert.Equal("Av. Paulista", updated.Address.Street);
        Assert.Equal("SP", updated.Address.State);
        Assert.False(updated.IsTrial);
        Assert.False(updated.IsTrialReadOnly);

        var loaded = await harness.Service.GetByIdAsync(target.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("Quadra Norte", loaded.TradeName);
        Assert.Equal("Sao Paulo", loaded.Address.City);

        var untouched = await harness.Service.GetByIdAsync(other.Id, CancellationToken.None);
        Assert.NotNull(untouched);
        Assert.Null(untouched.TradeName);
        Assert.Null(untouched.ContactEmail);
        Assert.Null(untouched.ContactPhone);
        Assert.Null(untouched.Address.Street);
        Assert.Null(untouched.Address.State);
    }

    [Fact]
    public async Task Update_without_company_fields_leaves_them_unchanged()
    {
        await using var harness = await Harness.CreateAsync();
        var (target, _) = await harness.CreatePairAsync();
        await new AdminTenantsController(harness.Service).Update(
            target.Id,
            UpdateRequest(target, request => request with
            {
                TradeName = "Quadra Norte",
                ContactEmail = "contato@quadra.com.br",
                Address = SampleAddress,
            }),
            CancellationToken.None);

        var action = await new AdminTenantsController(harness.Service).Update(
            target.Id,
            UpdateRequest(target, request => request with { LegalName = "Renamed Ltda" }),
            CancellationToken.None);

        var updated = Assert.IsType<TenantAdminResponseDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        Assert.Equal("Renamed Ltda", updated.LegalName);
        Assert.Equal("Quadra Norte", updated.TradeName);
        Assert.Equal("contato@quadra.com.br", updated.ContactEmail);
        Assert.Equal("Av. Paulista", updated.Address.Street);
    }

    [Fact]
    public async Task Update_with_invalid_email_returns_400_and_changes_nothing()
    {
        await using var harness = await Harness.CreateAsync();
        var (target, _) = await harness.CreatePairAsync();

        var action = await new AdminTenantsController(harness.Service).Update(
            target.Id,
            UpdateRequest(target, request => request with
            {
                TradeName = "Should Not Persist",
                ContactEmail = "not-an-email",
            }),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
        var loaded = await harness.Service.GetByIdAsync(target.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Null(loaded.TradeName);
        Assert.Null(loaded.ContactEmail);
    }

    [Fact]
    public async Task Update_with_invalid_state_returns_400()
    {
        await using var harness = await Harness.CreateAsync();
        var (target, _) = await harness.CreatePairAsync();

        var action = await new AdminTenantsController(harness.Service).Update(
            target.Id,
            UpdateRequest(target, request => request with
            {
                Address = SampleAddress with { State = "SAO" },
            }),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action.Result);
    }

    private static CreateTenantRequestDto CreateRequest(string slug)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new CreateTenantRequestDto
        {
            LegalName = slug,
            TaxId = $"99{suffix}000191",
            Subdomain = $"{slug}-{suffix}",
            ActiveModules = [PlatformModules.Rentals, PlatformModules.Catalog],
            AssetFamilyKeys = [AssetFamilyKeys.Generic],
        };
    }

    private static UpdateTenantRequestDto UpdateRequest(
        TenantAdminResponseDto tenant,
        Func<UpdateTenantRequestDto, UpdateTenantRequestDto> customize) =>
        customize(new UpdateTenantRequestDto
        {
            LegalName = tenant.LegalName,
            TaxId = tenant.TaxId,
            Subdomain = tenant.Subdomain ?? throw new InvalidOperationException("Subdomain is required."),
            ActiveModules = [PlatformModules.Rentals, PlatformModules.Catalog],
            AssetFamilyKeys = [AssetFamilyKeys.Generic],
        });

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

        public async Task<(TenantAdminResponseDto Target, TenantAdminResponseDto Other)> CreatePairAsync()
        {
            var previous = TenantProvider.TenantId;
            TenantProvider.TenantId = null;
            var target = await Service.CreateAsync(CreateRequest("target"), CancellationToken.None);
            var other = await Service.CreateAsync(CreateRequest("other"), CancellationToken.None);
            TenantProvider.TenantId = previous;
            return (target, other);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
