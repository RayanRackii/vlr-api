using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CustomerGroupVisibilityTests
{
    private sealed record Seed(CatalogHarness H, CustomerGroupService Groups, CatalogProduct Pub, CatalogProduct Direct,
        CatalogProduct ViaGroup, Customer Allowed, Customer GroupMember, Customer Other);

    private static async Task<Seed> SeedAsync()
    {
        var h = await CatalogHarness.CreateAsync();
        var groups = new CustomerGroupService(h.Db, h.TenantProvider);
        var pub = new CatalogProduct { TenantId = h.Tenant.Id, Name = "A Público" };
        var direct = new CatalogProduct { TenantId = h.Tenant.Id, Name = "B Exclusivo direto" };
        var viaGroup = new CatalogProduct { TenantId = h.Tenant.Id, Name = "C Exclusivo grupo" };
        h.Db.CatalogProducts.AddRange(pub, direct, viaGroup);
        var allowed = h.AddCustomer("Ana");
        var member = h.AddCustomer("Bia");
        var other = h.AddCustomer("Caio");
        await h.Db.SaveChangesAsync();

        var g = await groups.CreateAsync(new UpsertCustomerGroupRequest("VIP", null), CancellationToken.None);
        await groups.AddMembersAsync(g.Id, new CustomerGroupMembersRequest([member.Id]), CancellationToken.None);
        await groups.SetProductAccessAsync(direct.Id, new UpdateCatalogProductAccessRequest("Restricted", [allowed.Id], null), CancellationToken.None);
        await groups.SetProductAccessAsync(viaGroup.Id, new UpdateCatalogProductAccessRequest("Restricted", null, [g.Id]), CancellationToken.None);
        return new Seed(h, groups, pub, direct, viaGroup, allowed, member, other);
    }

    private static async Task<Guid[]> ListIds(Seed s, Guid? customerId, string? search = null) =>
        (await s.H.Portal.ListProductsAsync(customerId, search, CancellationToken.None)).Select(p => p.Id).ToArray();

    [Fact]
    public async Task List_filters_by_visibility_per_customer()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        Assert.Equal(new[] { s.Pub.Id, s.Direct.Id }, await ListIds(s, s.Allowed.Id));
        Assert.Equal(new[] { s.Pub.Id, s.ViaGroup.Id }, await ListIds(s, s.GroupMember.Id));
        Assert.Equal(new[] { s.Pub.Id }, await ListIds(s, s.Other.Id));
        Assert.Equal(new[] { s.Pub.Id }, await ListIds(s, null));
        Assert.Empty(await ListIds(s, s.Other.Id, "exclusivo"));
        Assert.Equal(new[] { s.Direct.Id }, await ListIds(s, s.Allowed.Id, "exclusivo"));
    }

    [Fact]
    public async Task Detail_hides_restricted_product_from_others()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        Assert.NotNull(await s.H.Portal.GetProductAsync(s.Allowed.Id, s.Direct.Id, CancellationToken.None));
        Assert.Null(await s.H.Portal.GetProductAsync(s.Other.Id, s.Direct.Id, CancellationToken.None));
        Assert.Null(await s.H.Portal.GetProductAsync(null, s.ViaGroup.Id, CancellationToken.None));
        Assert.NotNull(await s.H.Portal.GetProductAsync(s.GroupMember.Id, s.ViaGroup.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Checkout_rejects_restricted_product_as_unavailable_and_keeps_existing_orders()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        CreatePortalOrderRequest Req(Guid id) => new() { Items = [new CreatePortalOrderItemRequest { ProductId = id, Quantity = 1 }] };

        await Assert.ThrowsAsync<ArgumentException>(() => s.H.Portal.CreateOrderAsync(s.Other.Id, Req(s.Direct.Id), CancellationToken.None));
        var order = await s.H.Portal.CreateOrderAsync(s.Allowed.Id, Req(s.Direct.Id), CancellationToken.None);

        await s.Groups.SetProductAccessAsync(s.Direct.Id, new UpdateCatalogProductAccessRequest("Restricted", null, null), CancellationToken.None);
        Assert.NotNull(await s.H.Portal.GetOrderAsync(s.Allowed.Id, order.Id, CancellationToken.None));
        Assert.Null(await s.H.Portal.GetProductAsync(s.Allowed.Id, s.Direct.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Cross_tenant_members_and_targets_are_rejected()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        var otherTenant = new Tenant("Other", "55555555000191");
        s.H.Db.Tenants.Add(otherTenant);
        var foreign = new Customer { TenantId = otherTenant.Id, Name = "X", Email = "x@o.test", Phone = "+5511999990001" };
        var foreignGroup = new CustomerGroup { TenantId = otherTenant.Id, Name = "Foreign" };
        s.H.Db.Customers.Add(foreign);
        s.H.Db.CustomerGroups.Add(foreignGroup);
        await s.H.Db.SaveChangesAsync();
        var g = (await s.Groups.ListAsync(CancellationToken.None)).Single();

        await Assert.ThrowsAsync<ArgumentException>(() => s.Groups.AddMembersAsync(g.Id, new([foreign.Id]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Groups.SetProductAccessAsync(s.Pub.Id, new("Restricted", [foreign.Id], null), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Groups.SetProductAccessAsync(s.Pub.Id, new("Restricted", null, [foreignGroup.Id]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => s.Groups.SetProductAccessAsync(s.Pub.Id, new("Bogus", null, null), CancellationToken.None));
        Assert.Null(await s.Groups.GetAsync(foreignGroup.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Group_name_is_unique_per_tenant()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Groups.CreateAsync(new("vip", null), CancellationToken.None));
        Assert.Equal(CatalogProductVisibility.Public, s.Pub.Visibility);
    }

    [Fact]
    public async Task Customer_of_another_tenant_sees_no_restricted_product()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        // A customer id from tenant B, even if granted in B, never unlocks tenant A's restricted products.
        var foreignCustomerId = Guid.NewGuid();
        s.H.Db.CatalogProductAccess.Add(new CatalogProductAccess { TenantId = Guid.NewGuid(), ProductId = s.Direct.Id, CustomerId = foreignCustomerId });
        await s.H.Db.SaveChangesAsync();
        Assert.Equal(new[] { s.Pub.Id }, await ListIds(s, foreignCustomerId));
        Assert.Null(await s.H.Portal.GetProductAsync(foreignCustomerId, s.Direct.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_group_removes_its_grants_and_writes_audit()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        var g = (await s.Groups.ListAsync(CancellationToken.None)).Single();
        Assert.True(await s.Groups.DeleteAsync(g.Id, CancellationToken.None));
        Assert.Equal(new[] { s.Pub.Id }, await ListIds(s, s.GroupMember.Id));
        var history = await s.Groups.ListHistoryAsync("group", g.Id, CancellationToken.None);
        Assert.Contains(history, h => h.Action == "group.deleted");
        Assert.Contains(history, h => h.Action == "group.membersAdded");
        Assert.NotEmpty(await s.Groups.ListHistoryAsync("product", s.Direct.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Access_response_flags_restricted_without_grants_and_staff_dto_has_counts()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        var r = await s.Groups.SetProductAccessAsync(s.Pub.Id, new("Restricted", null, null), CancellationToken.None);
        Assert.True(r!.RestrictedWithoutGrants);
        var staff = await s.H.Products.GetByIdAsync(s.Direct.Id, CancellationToken.None);
        Assert.Equal(CatalogProductVisibility.Restricted, staff!.Visibility);
        Assert.Equal(1, staff.GrantedCustomerCount);
        Assert.Equal(0, staff.GrantedGroupCount);
        await Assert.ThrowsAsync<ArgumentException>(() => s.Groups.AddMembersAsync(s.H.Db.CustomerGroups.First().Id,
            new(Enumerable.Range(0, CustomerGroupService.MaxIdsPerRequest + 1).Select(_ => Guid.NewGuid()).ToList()), CancellationToken.None));
    }

    [Fact]
    public async Task Restricted_product_files_use_signed_urls_public_products_keep_public_urls()
    {
        var s = await SeedAsync();
        await using var _ = s.H;
        foreach (var p in new[] { s.Pub, s.Direct })
        {
            s.H.Db.Set<CatalogProductFile>().Add(new CatalogProductFile
            {
                TenantId = s.H.Tenant.Id, ProductId = p.Id, StorageKey = $"k/{p.Id}.png", FileName = "a.png",
                MimeType = "image/png", SizeBytes = 1, Visibility = CatalogFileVisibility.CustomerVisible,
            });
        }
        await s.H.Db.SaveChangesAsync();
        var list = await s.H.Portal.ListProductsAsync(s.Allowed.Id, null, CancellationToken.None);
        Assert.Contains("sig=", list.Single(p => p.Id == s.Direct.Id).Files.Single().Url);
        Assert.DoesNotContain("sig=", list.Single(p => p.Id == s.Pub.Id).Files.Single().Url);
    }
}
