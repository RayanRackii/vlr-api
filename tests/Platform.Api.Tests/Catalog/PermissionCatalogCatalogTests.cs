using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Catalog;

public sealed class PermissionCatalogCatalogTests
{
    [Fact]
    public void Catalog_adds_six_keys_and_keeps_default_user_bundle_unchanged()
    {
        Assert.Equal(43, PermissionCatalog.All.Length);
        Assert.Equal(43, PermissionCatalog.AllKeys.Count);
        Assert.Contains(Permissions.Catalog.ProductsRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.ProductsManage, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.OrdersRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.OrdersManage, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.NotificationsRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.NotificationsResend, PermissionCatalog.AllKeys);
        Assert.DoesNotContain(Permissions.Catalog.ProductsRead, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Catalog.OrdersManage, PermissionCatalog.DefaultUserKeys);
    }

    [Fact]
    public void PlatformModules_normalizes_catalog_aliases()
    {
        Assert.True(PlatformModules.TryNormalize("Catálogo", out var a) && a == PlatformModules.Catalog);
        Assert.True(PlatformModules.TryNormalize("orders", out var b) && b == PlatformModules.Catalog);
        Assert.True(PlatformModules.TryNormalize("pedidos", out var c) && c == PlatformModules.Catalog);
        Assert.True(PlatformModules.TryNormalize("catalogo", out var d) && d == PlatformModules.Catalog);
    }
}
