using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Admin;

public sealed class PlatformModuleCatalogTests
{
    private const string AssetRegistry = "asset-registry";

    [Fact]
    public void Catalog_required_capabilities_are_empty()
    {
        var catalog = Descriptor(PlatformModules.Catalog);

        Assert.Empty(catalog.RequiredCapabilities);
        Assert.Empty(catalog.Provides);
        Assert.True(catalog.IsCommercial);
        Assert.False(catalog.IsLegacy);
    }

    [Fact]
    public void Inventory_provides_asset_registry_and_requires_nothing()
    {
        var inventory = Descriptor(PlatformModules.Inventory);

        Assert.Equal([AssetRegistry], inventory.Provides);
        Assert.Empty(inventory.RequiredCapabilities);
        Assert.True(inventory.IsCommercial);
        Assert.False(inventory.IsLegacy);
    }

    [Theory]
    [InlineData(PlatformModules.Rentals)]
    [InlineData(PlatformModules.Pmoc)]
    [InlineData(PlatformModules.WorkOrders)]
    public void Dependent_modules_require_asset_registry_and_do_not_provide_it(string key)
    {
        var module = Descriptor(key);

        Assert.Equal([AssetRegistry], module.RequiredCapabilities);
        Assert.Empty(module.Provides);
        Assert.True(module.IsCommercial);
        Assert.False(module.IsLegacy);
    }

    [Fact]
    public void Orders_and_pedidos_normalize_to_catalog()
    {
        Assert.True(PlatformModules.TryNormalize("orders", out var orders));
        Assert.Equal(PlatformModules.Catalog, orders);
        Assert.True(PlatformModules.TryNormalize("pedidos", out var pedidos));
        Assert.Equal(PlatformModules.Catalog, pedidos);
        Assert.True(PlatformModuleCatalog.TryNormalize("Catálogo", out var labeled));
        Assert.Equal(PlatformModules.Catalog, labeled);
    }

    [Fact]
    public void Asset_registry_is_not_a_tenant_module_key()
    {
        Assert.False(PlatformModules.TryNormalize(AssetRegistry, out _));
        Assert.DoesNotContain(
            PlatformModuleCatalog.All,
            m => string.Equals(m.Key, AssetRegistry, StringComparison.Ordinal));
    }

    [Fact]
    public void Existing_maintenance_rows_remain_readable()
    {
        Assert.True(PlatformModules.TryNormalize("maintenance", out var a));
        Assert.Equal(PlatformModules.Maintenance, a);
        Assert.True(PlatformModules.TryNormalize("manutenção", out var b));
        Assert.Equal(PlatformModules.Maintenance, b);

        var legacy = Descriptor(PlatformModules.Maintenance);
        Assert.True(legacy.IsLegacy);
        Assert.False(legacy.IsCommercial);
    }

    [Fact]
    public void Unknown_module_throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlatformModuleCatalog.NormalizeEntitlements(["not-a-module"]));

        Assert.Contains("Unknown module", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not-a-module", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_only_is_commercially_valid()
    {
        var normalized = PlatformModuleCatalog.NormalizeEntitlements(["catalog"]);

        Assert.Equal([PlatformModules.Catalog], normalized);
        Assert.DoesNotContain(PlatformModules.Inventory, normalized);
    }

    [Fact]
    public void Orders_alias_normalizes_to_catalog_in_entitlements()
    {
        var normalized = PlatformModuleCatalog.NormalizeEntitlements(["orders", "pedidos"]);

        Assert.Equal([PlatformModules.Catalog], normalized);
    }

    [Theory]
    [InlineData("rentals")]
    [InlineData("pmoc")]
    [InlineData("os")]
    public void Dependent_module_without_inventory_does_not_gain_inventory(string module)
    {
        var normalized = PlatformModuleCatalog.NormalizeEntitlements([module]);

        Assert.Equal([Canonical(module)], normalized);
        Assert.DoesNotContain(PlatformModules.Inventory, normalized);
    }

    [Fact]
    public void Inventory_only_is_commercially_valid()
    {
        var normalized = PlatformModuleCatalog.NormalizeEntitlements(["Inventory"]);

        Assert.Equal([PlatformModules.Inventory], normalized);
    }

    [Fact]
    public void Deactivating_inventory_while_rentals_remain_is_allowed()
    {
        var normalized = PlatformModuleCatalog.NormalizeEntitlements(
            ["rentals"],
            existingActiveKeys: [PlatformModules.Inventory, PlatformModules.Rentals]);

        Assert.Equal([PlatformModules.Rentals], normalized);
        Assert.DoesNotContain(PlatformModules.Inventory, normalized);
        Assert.True(
            PlatformModuleCatalog.ShouldRemoveStoredModule(
                PlatformModules.Inventory,
                normalized));
    }

    [Fact]
    public void Maintenance_cannot_be_newly_enabled()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlatformModuleCatalog.NormalizeEntitlements(["maintenance", "rentals"]));

        Assert.Contains(
            "cannot activate legacy module maintenance",
            ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Maintenance_cannot_be_added_when_tenant_does_not_already_have_it()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => PlatformModuleCatalog.NormalizeEntitlements(
                ["rentals", "manutenção"],
                existingActiveKeys: [PlatformModules.Rentals]));

        Assert.Contains(
            "cannot activate legacy module maintenance",
            ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Existing_maintenance_is_kept_when_payload_omits_it()
    {
        var normalized = PlatformModuleCatalog.NormalizeEntitlements(
            ["rentals"],
            existingActiveKeys: [PlatformModules.Maintenance, PlatformModules.Rentals]);

        Assert.Equal([PlatformModules.Rentals], normalized);
        Assert.False(
            PlatformModuleCatalog.ShouldRemoveStoredModule(
                PlatformModules.Maintenance,
                normalized));
    }

    [Fact]
    public void Existing_maintenance_is_kept_when_payload_still_includes_it()
    {
        var normalized = PlatformModuleCatalog.NormalizeEntitlements(
            ["rentals", "maintenance"],
            existingActiveKeys: [PlatformModules.Maintenance, PlatformModules.Rentals]);

        Assert.Equal([PlatformModules.Rentals], normalized);
        Assert.DoesNotContain(PlatformModules.Maintenance, normalized);
        Assert.False(
            PlatformModuleCatalog.ShouldRemoveStoredModule(
                PlatformModules.Maintenance,
                normalized));
    }

    private static PlatformModuleDescriptor Descriptor(string key) =>
        Assert.Single(PlatformModuleCatalog.All, m => m.Key == key);

    private static string Canonical(string module)
    {
        Assert.True(PlatformModules.TryNormalize(module, out var canonical));
        return canonical;
    }
}
