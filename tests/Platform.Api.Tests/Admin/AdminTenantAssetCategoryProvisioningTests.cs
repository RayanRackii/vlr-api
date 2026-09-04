using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

public sealed class AdminTenantAssetCategoryProvisioningTests
{
    [Fact]
    public async Task Create_spaces_seeds_Quadra()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("spaces-club", [PlatformModules.Rentals], [AssetFamilyKeys.Spaces]),
            CancellationToken.None);

        Assert.Equal(["Quadra"], await harness.CategoryNamesAsync(created.Id));
        Assert.DoesNotContain(created.ActiveModules, m => m.ModuleName == PlatformModules.Inventory);
    }

    [Fact]
    public async Task Create_electrical_seeds_Quadro_eletrico()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("electrical-club", [PlatformModules.Rentals], [AssetFamilyKeys.Electrical]),
            CancellationToken.None);

        Assert.Equal(["Quadro elétrico"], await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Create_goods_seeds_Cacamba()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("goods-club", [PlatformModules.Rentals], [AssetFamilyKeys.Goods]),
            CancellationToken.None);

        Assert.Equal(["Caçamba"], await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Create_generic_seeds_zero_categories()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("generic-club", [PlatformModules.Rentals], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        Assert.Empty(await harness.CategoryNamesAsync(created.Id));
        Assert.Equal([AssetFamilyKeys.Generic], created.AssetFamilyKeys);
    }

    [Fact]
    public async Task Create_combination_seeds_each_supported_family_once()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest(
                "combo-club",
                [PlatformModules.Rentals],
                [
                    AssetFamilyKeys.Spaces,
                    AssetFamilyKeys.Electrical,
                    AssetFamilyKeys.Goods,
                    AssetFamilyKeys.Generic,
                ]),
            CancellationToken.None);

        Assert.Equal(
            ["Caçamba", "Quadra", "Quadro elétrico"],
            await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Create_omitted_family_keys_defaults_to_generic_with_zero_categories()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("omitted-club", [PlatformModules.Catalog], assetFamilyKeys: null),
            CancellationToken.None);

        Assert.Equal([AssetFamilyKeys.Generic], created.AssetFamilyKeys);
        Assert.Empty(await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Update_generic_plus_electrical_seeds_Quadro_eletrico_once()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("edit-club", [PlatformModules.Rentals], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        await harness.Service.UpdateAsync(
            created.Id,
            UpdateRequest(
                created,
                [PlatformModules.Rentals],
                [AssetFamilyKeys.Generic, AssetFamilyKeys.Electrical]),
            CancellationToken.None);

        Assert.Equal(["Quadro elétrico"], await harness.CategoryNamesAsync(created.Id));

        await harness.Service.UpdateAsync(
            created.Id,
            UpdateRequest(
                created,
                [PlatformModules.Rentals],
                [AssetFamilyKeys.Generic, AssetFamilyKeys.Electrical]),
            CancellationToken.None);

        Assert.Equal(["Quadro elétrico"], await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Update_adding_multiple_supported_families_seeds_each_example()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("multi-edit-club", [PlatformModules.Rentals], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        await harness.Service.UpdateAsync(
            created.Id,
            UpdateRequest(
                created,
                [PlatformModules.Rentals],
                [AssetFamilyKeys.Spaces, AssetFamilyKeys.Electrical, AssetFamilyKeys.Goods]),
            CancellationToken.None);

        Assert.Equal(
            ["Caçamba", "Quadra", "Quadro elétrico"],
            await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Create_pmoc_with_generic_only_throws()
    {
        await using var harness = await Harness.CreateAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.CreateAsync(
                CreateRequest("pmoc-generic", [PlatformModules.Pmoc], [AssetFamilyKeys.Generic]),
                CancellationToken.None));

        Assert.Equal(
            "PMOC requires at least one asset family with available resource types.",
            ex.Message);
    }

    [Fact]
    public async Task Create_pmoc_with_omitted_keys_defaults_to_generic_and_throws()
    {
        await using var harness = await Harness.CreateAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.CreateAsync(
                CreateRequest("pmoc-omitted", [PlatformModules.Pmoc], assetFamilyKeys: null),
                CancellationToken.None));

        Assert.Equal(
            "PMOC requires at least one asset family with available resource types.",
            ex.Message);
    }

    [Theory]
    [InlineData(AssetFamilyKeys.Spaces)]
    [InlineData(AssetFamilyKeys.Electrical)]
    [InlineData(AssetFamilyKeys.Goods)]
    public async Task Create_pmoc_with_a_provisioning_family_is_valid(string familyKey)
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest($"pmoc-{familyKey}", [PlatformModules.Pmoc], [familyKey]),
            CancellationToken.None);

        Assert.Contains(created.ActiveModules, m => m.ModuleName == PlatformModules.Pmoc);
        Assert.DoesNotContain(created.ActiveModules, m => m.ModuleName == PlatformModules.Inventory);
        Assert.NotEmpty(await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Create_pmoc_with_generic_and_electrical_is_valid()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest(
                "pmoc-mixed",
                [PlatformModules.Pmoc],
                [AssetFamilyKeys.Generic, AssetFamilyKeys.Electrical]),
            CancellationToken.None);

        Assert.Equal(["Quadro elétrico"], await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Update_pmoc_from_electrical_to_generic_only_throws()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest(
                "pmoc-edit",
                [PlatformModules.Pmoc],
                [AssetFamilyKeys.Electrical]),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.UpdateAsync(
                created.Id,
                UpdateRequest(created, [PlatformModules.Pmoc], [AssetFamilyKeys.Generic]),
                CancellationToken.None));

        Assert.Equal(
            "PMOC requires at least one asset family with available resource types.",
            ex.Message);
        Assert.Equal(["Quadro elétrico"], await harness.CategoryNamesAsync(created.Id));
        Assert.Equal([AssetFamilyKeys.Electrical], await harness.FamilyKeysAsync(created.Id));
    }

    [Fact]
    public async Task Create_os_with_generic_only_is_valid()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("os-generic", [PlatformModules.WorkOrders], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        Assert.Contains(created.ActiveModules, m => m.ModuleName == PlatformModules.WorkOrders);
        Assert.Empty(await harness.CategoryNamesAsync(created.Id));
        Assert.DoesNotContain(created.ActiveModules, m => m.ModuleName == PlatformModules.Inventory);
    }

    [Fact]
    public async Task Create_rentals_with_generic_only_is_valid()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("rentals-generic", [PlatformModules.Rentals], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        Assert.Contains(created.ActiveModules, m => m.ModuleName == PlatformModules.Rentals);
        Assert.Empty(await harness.CategoryNamesAsync(created.Id));
        Assert.DoesNotContain(created.ActiveModules, m => m.ModuleName == PlatformModules.Inventory);
    }

    [Fact]
    public async Task Create_catalog_only_is_valid_without_inventory_or_categories()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("catalog-only", [PlatformModules.Catalog], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        Assert.Equal([PlatformModules.Catalog], created.ActiveModules.Select(m => m.ModuleName));
        Assert.Empty(await harness.CategoryNamesAsync(created.Id));
        Assert.DoesNotContain(
            created.ActiveModules,
            m => m.ModuleName == PlatformModuleCatalog.AssetRegistryCapability);
    }

    [Fact]
    public async Task Create_does_not_insert_inventory_unless_requested()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest(
                "no-inv",
                [PlatformModules.Rentals, PlatformModules.Pmoc, PlatformModules.WorkOrders],
                [AssetFamilyKeys.Spaces]),
            CancellationToken.None);

        var moduleNames = created.ActiveModules.Select(m => m.ModuleName).ToList();
        Assert.DoesNotContain(PlatformModules.Inventory, moduleNames);
        Assert.Contains(PlatformModules.Rentals, moduleNames);
        Assert.Contains(PlatformModules.Pmoc, moduleNames);
        Assert.Contains(PlatformModules.WorkOrders, moduleNames);
    }

    [Fact]
    public async Task Create_inserts_inventory_when_requested()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("with-inv", [PlatformModules.Inventory], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        Assert.Contains(created.ActiveModules, m => m.ModuleName == PlatformModules.Inventory);
        Assert.Empty(await harness.CategoryNamesAsync(created.Id));
    }

    [Fact]
    public async Task Create_cannot_newly_enable_maintenance()
    {
        await using var harness = await Harness.CreateAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.CreateAsync(
                CreateRequest("new-maint", [PlatformModules.Maintenance], [AssetFamilyKeys.Generic]),
                CancellationToken.None));

        Assert.Contains("cannot activate legacy module maintenance", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_preserves_existing_maintenance_when_payload_omits_it()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Service.CreateAsync(
            CreateRequest("keep-maint", [PlatformModules.Catalog], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        harness.Db.TenantModules.Add(
            new TenantModule(created.Id, PlatformModules.Maintenance, isActive: true));
        await harness.Db.SaveChangesAsync();

        var updated = await harness.Service.UpdateAsync(
            created.Id,
            UpdateRequest(created, [PlatformModules.Catalog], [AssetFamilyKeys.Generic]),
            CancellationToken.None);

        var stored = await harness.Db.TenantModules
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == created.Id)
            .Select(m => m.ModuleName)
            .ToListAsync();

        Assert.Contains(PlatformModules.Maintenance, stored);
        Assert.Contains(PlatformModules.Catalog, stored);
        Assert.DoesNotContain(updated.ActiveModules, m => m.ModuleName == PlatformModules.Inventory);
    }

    private static CreateTenantRequestDto CreateRequest(
        string slug,
        IReadOnlyList<string> modules,
        IReadOnlyList<string>? assetFamilyKeys)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new CreateTenantRequestDto
        {
            LegalName = slug,
            TaxId = $"99{suffix}000191",
            Subdomain = $"{slug}-{suffix}",
            ActiveModules = modules,
            AssetFamilyKeys = assetFamilyKeys,
        };
    }

    private static UpdateTenantRequestDto UpdateRequest(
        TenantAdminResponseDto tenant,
        IReadOnlyList<string> modules,
        IReadOnlyList<string> assetFamilyKeys) =>
        new()
        {
            LegalName = tenant.LegalName,
            TaxId = tenant.TaxId,
            Subdomain = tenant.Subdomain ?? throw new InvalidOperationException("Subdomain is required."),
            ActiveModules = modules,
            AssetFamilyKeys = assetFamilyKeys,
        };

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(AppDbContext db, AdminTenantService service)
        {
            Db = db;
            Service = service;
        }

        public AppDbContext Db { get; }

        public AdminTenantService Service { get; }

        public static async Task<Harness> CreateAsync()
        {
            var tenantProvider = new FakeTenantProvider { TenantId = null };
            var db = InMemoryAppDb.Create(tenantProvider);
            SeedCatalogFamilies(db);
            await db.SaveChangesAsync();

            var service = new AdminTenantService(
                db,
                new FakeTenantUserAdminService(),
                new FakePlatformAdminMembershipService(),
                new FakeTenantAccessBootstrapper(),
                new FakeSupabaseAuthAdminClient(),
                Options.Create(new PlatformAdminOptions()),
                NullLogger<AdminTenantService>.Instance);

            return new Harness(db, service);
        }

        public async Task<IReadOnlyList<string>> CategoryNamesAsync(Guid tenantId)
        {
            return await Db.AssetCategories
                .IgnoreQueryFilters()
                .Where(c => c.TenantId == tenantId)
                .OrderBy(c => c.Name)
                .Select(c => c.Name)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<string>> FamilyKeysAsync(Guid tenantId)
        {
            return await Db.TenantAssetFamilies
                .IgnoreQueryFilters()
                .Where(t => t.TenantId == tenantId)
                .Join(
                    Db.AssetFamilies.AsNoTracking(),
                    t => t.FamilyId,
                    f => f.Id,
                    (_, f) => f)
                .OrderBy(f => f.SortOrder)
                .Select(f => f.Key)
                .ToListAsync();
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static void SeedCatalogFamilies(AppDbContext db)
        {
            AddFamily(db, AssetFamilyKeys.Ids.Spaces, AssetFamilyKeys.Spaces, "Espaços", 1);
            AddFamily(db, AssetFamilyKeys.Ids.Electrical, AssetFamilyKeys.Electrical, "Elétrica", 2);
            AddFamily(db, AssetFamilyKeys.Ids.Goods, AssetFamilyKeys.Goods, "Bens", 3);
            AddFamily(db, AssetFamilyKeys.Ids.Generic, AssetFamilyKeys.Generic, "Genérico", 4);
        }

        private static void AddFamily(
            AppDbContext db,
            Guid id,
            string key,
            string label,
            int sortOrder)
        {
            var family = new AssetFamily
            {
                Key = key,
                Label = label,
                FieldSchemaJson = """{"fields":[]}""",
                SortOrder = sortOrder,
                IsActive = true,
            };

            typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(family, id);
            db.AssetFamilies.Add(family);
        }
    }
}
