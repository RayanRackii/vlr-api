using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Catalog;

public sealed class PermissionCatalogCatalogTests
{
    [Fact]
    public void Catalog_includes_complete_permission_and_keeps_default_user_bundle_unchanged()
    {
        Assert.Equal(57, PermissionCatalog.All.Length);
        Assert.Equal(57, PermissionCatalog.AllKeys.Count);
        foreach (var key in new[] { Permissions.Rentals.SegmentsRead, Permissions.Rentals.SegmentsWrite,
            Permissions.Rentals.MapSettingsRead, Permissions.Rentals.MapSettingsWrite })
        {
            Assert.Contains(key, PermissionCatalog.AllKeys);
            Assert.Equal(PlatformModules.Rentals, Assert.Single(PermissionCatalog.All, item => item.Key == key).ModuleKey);
            Assert.DoesNotContain(key, PermissionCatalog.DefaultUserKeys);
            Assert.DoesNotContain(key, PermissionCatalog.TechnicianLegacyKeys);
        }
        foreach (var key in new[] { Permissions.Core.TaxonomyRead, Permissions.Core.TaxonomyManage })
        {
            Assert.Contains(key, PermissionCatalog.AllKeys);
            Assert.Null(Assert.Single(PermissionCatalog.All, item => item.Key == key).ModuleKey);
            Assert.DoesNotContain(key, PermissionCatalog.DefaultUserKeys);
            Assert.DoesNotContain(key, PermissionCatalog.TechnicianLegacyKeys);
        }
        Assert.Contains(Permissions.Core.UnitsWrite, PermissionCatalog.AllKeys);
        Assert.Null(Assert.Single(PermissionCatalog.All, item => item.Key == Permissions.Core.UnitsWrite).ModuleKey);
        Assert.DoesNotContain(Permissions.Core.UnitsWrite, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Core.UnitsWrite, PermissionCatalog.TechnicianLegacyKeys);
        Assert.Contains(Permissions.Core.CustomersRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Core.CustomersRead, PermissionCatalog.ExplicitGrantOnlyKeys);
        Assert.Contains(Permissions.Core.CustomersRead, PermissionCatalog.AdminWildcardExcludedKeys);
        Assert.DoesNotContain(Permissions.Core.CustomersRead, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Core.CustomersRead, PermissionCatalog.TechnicianLegacyKeys);
        Assert.Contains(Permissions.Catalog.ProductsRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.ProductsManage, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.OrdersRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.OrdersManage, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.NotificationsRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Catalog.NotificationsResend, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Rentals.ReservationsComplete, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Rentals.ScheduleLessonsWrite, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Rentals.ScheduleLessonsWrite, PermissionCatalog.ExplicitGrantOnlyKeys);
        Assert.Contains(Permissions.Core.NotificationsRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Core.NotificationsWrite, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Rentals.BookingPolicyRead, PermissionCatalog.AllKeys);
        Assert.Contains(Permissions.Rentals.BookingPolicyWrite, PermissionCatalog.AllKeys);
        Assert.DoesNotContain(Permissions.Rentals.BookingPolicyRead, PermissionCatalog.ExplicitGrantOnlyKeys);
        Assert.DoesNotContain(Permissions.Rentals.BookingPolicyWrite, PermissionCatalog.ExplicitGrantOnlyKeys);
        Assert.DoesNotContain(Permissions.Rentals.BookingPolicyRead, PermissionCatalog.AdminWildcardExcludedKeys);
        Assert.DoesNotContain(Permissions.Rentals.BookingPolicyWrite, PermissionCatalog.AdminWildcardExcludedKeys);
        Assert.DoesNotContain(Permissions.Rentals.BookingPolicyRead, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Rentals.BookingPolicyWrite, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Rentals.BookingPolicyRead, PermissionCatalog.TechnicianLegacyKeys);
        Assert.DoesNotContain(Permissions.Rentals.BookingPolicyWrite, PermissionCatalog.TechnicianLegacyKeys);
        Assert.DoesNotContain(Permissions.Catalog.ProductsRead, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Catalog.OrdersManage, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Rentals.ReservationsComplete, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Rentals.ReservationsComplete, PermissionCatalog.TechnicianLegacyKeys);
        Assert.DoesNotContain(Permissions.Rentals.ScheduleLessonsWrite, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Rentals.ScheduleLessonsWrite, PermissionCatalog.TechnicianLegacyKeys);
        Assert.DoesNotContain(Permissions.Core.NotificationsRead, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Core.NotificationsWrite, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Core.NotificationsRead, PermissionCatalog.TechnicianLegacyKeys);
        Assert.DoesNotContain(Permissions.Core.NotificationsWrite, PermissionCatalog.TechnicianLegacyKeys);
    }

    [Fact]
    public void Admin_wildcard_excluded_keys_are_explicit_grant_only()
    {
        Assert.NotEmpty(PermissionCatalog.AdminWildcardExcludedKeys);
        Assert.All(
            PermissionCatalog.AdminWildcardExcludedKeys,
            key => Assert.Contains(key, PermissionCatalog.ExplicitGrantOnlyKeys));
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
