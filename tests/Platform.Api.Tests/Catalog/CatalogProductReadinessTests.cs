using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogProductReadinessTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static async Task EnableRentalsAsync(CatalogHarness h)
    {
        h.Db.TenantModules.Add(new TenantModule(h.Tenant.Id, PlatformModules.Rentals, true));
        await h.Db.SaveChangesAsync();
    }

    private static async Task<CatalogProduct> AddProductAsync(
        CatalogHarness h,
        string name = "Item",
        decimal? price = 10m,
        bool usesSchedule = false,
        CatalogProductVisibility visibility = CatalogProductVisibility.Public,
        bool isActive = true)
    {
        var product = new CatalogProduct
        {
            TenantId = h.Tenant.Id,
            Name = name,
            Price = price,
            UsesSchedule = usesSchedule,
            Visibility = visibility,
            IsActive = isActive,
        };
        h.Db.CatalogProducts.Add(product);
        await h.Db.SaveChangesAsync();
        return product;
    }

    private static async Task AddRequirementAsync(CatalogHarness h, Guid productId, Guid? assetId, bool active = true)
    {
        h.Db.CatalogProductResourceRequirements.Add(new CatalogProductResourceRequirement
        {
            TenantId = h.Tenant.Id,
            ProductId = productId,
            RentalAssetId = assetId,
            ResourceQuantity = 1,
            DurationMinutesPerUnit = 30,
            IsActive = active,
            Version = 0,
        });
        await h.Db.SaveChangesAsync();
    }

    private static async Task<List<Guid>> PortalIdsAsync(CatalogHarness h, Guid? customerId = null) =>
        (await h.Portal.ListProductsAsync(customerId, null, Ct)).Select(p => p.Id).ToList();

    [Fact]
    public async Task Portal_hides_scheduled_product_without_resource_when_rentals_active()
    {
        await using var h = await CatalogHarness.CreateAsync();
        await EnableRentalsAsync(h);
        var bare = await AddProductAsync(h, "Sem recurso", usesSchedule: true);
        var linked = await AddProductAsync(h, "Com recurso", usesSchedule: true);
        await AddRequirementAsync(h, linked.Id, Guid.NewGuid());
        var inactiveOnly = await AddProductAsync(h, "Recurso inativo", usesSchedule: true);
        await AddRequirementAsync(h, inactiveOnly.Id, Guid.NewGuid(), active: false);
        var categoryOnly = await AddProductAsync(h, "So categoria", usesSchedule: true);
        await AddRequirementAsync(h, categoryOnly.Id, null);

        var ids = await PortalIdsAsync(h);

        Assert.Equal([linked.Id], ids);
        Assert.Null(await h.Portal.GetProductAsync(null, bare.Id, Ct));
        Assert.NotNull(await h.Portal.GetProductAsync(null, linked.Id, Ct));
    }

    [Fact]
    public async Task Portal_does_not_require_resource_when_rentals_module_is_off()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var bare = await AddProductAsync(h, "Sem recurso", usesSchedule: true);

        Assert.Contains(bare.Id, await PortalIdsAsync(h));
    }

    [Fact]
    public async Task Portal_keeps_unscheduled_product_without_resource()
    {
        await using var h = await CatalogHarness.CreateAsync();
        await EnableRentalsAsync(h);
        var plain = await AddProductAsync(h, "Simples", usesSchedule: false);

        Assert.Contains(plain.Id, await PortalIdsAsync(h));
    }

    [Fact]
    public async Task Portal_hides_restricted_product_without_grants_even_for_customer()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var customer = h.AddCustomer();
        await h.Db.SaveChangesAsync();
        var noGrants = await AddProductAsync(h, "Fechado", visibility: CatalogProductVisibility.Restricted);
        var granted = await AddProductAsync(h, "Concedido", visibility: CatalogProductVisibility.Restricted);
        h.Db.CatalogProductAccess.Add(new CatalogProductAccess
        {
            TenantId = h.Tenant.Id,
            ProductId = granted.Id,
            CustomerId = customer.Id,
        });
        await h.Db.SaveChangesAsync();

        var ids = await PortalIdsAsync(h, customer.Id);

        Assert.DoesNotContain(noGrants.Id, ids);
        Assert.Contains(granted.Id, ids);
    }

    [Fact]
    public async Task Portal_hides_restricted_product_granted_only_to_an_empty_group()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var customer = h.AddCustomer();
        var group = new CustomerGroup { TenantId = h.Tenant.Id, Name = "Vazio" };
        h.Db.CustomerGroups.Add(group);
        await h.Db.SaveChangesAsync();
        var product = await AddProductAsync(h, "Grupo vazio", visibility: CatalogProductVisibility.Restricted);
        h.Db.CatalogProductAccess.Add(new CatalogProductAccess
        {
            TenantId = h.Tenant.Id,
            ProductId = product.Id,
            GroupId = group.Id,
        });
        await h.Db.SaveChangesAsync();

        Assert.DoesNotContain(product.Id, await PortalIdsAsync(h, customer.Id));

        h.Db.CustomerGroupMembers.Add(new CustomerGroupMember
        {
            TenantId = h.Tenant.Id,
            GroupId = group.Id,
            CustomerId = customer.Id,
        });
        await h.Db.SaveChangesAsync();

        Assert.Contains(product.Id, await PortalIdsAsync(h, customer.Id));
    }

    [Fact]
    public async Task Portal_hides_non_positive_price_and_keeps_null_price_and_complete_products()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var zero = await AddProductAsync(h, "Zero", price: 0m);
        var negative = await AddProductAsync(h, "Negativo", price: -1m);
        var onRequest = await AddProductAsync(h, "Sob consulta", price: null);
        var complete = await AddProductAsync(h, "Completo", price: 25m);

        var ids = await PortalIdsAsync(h);

        Assert.DoesNotContain(zero.Id, ids);
        Assert.DoesNotContain(negative.Id, ids);
        Assert.Contains(onRequest.Id, ids);
        Assert.Contains(complete.Id, ids);
    }

    [Fact]
    public async Task Portal_order_creation_refuses_a_product_that_is_not_ready()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var customer = h.AddCustomer();
        await h.Db.SaveChangesAsync();
        var zero = await AddProductAsync(h, "Zero", price: 0m);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Portal.CreateOrderAsync(
            customer.Id,
            new CreatePortalOrderRequest { Items = [new CreatePortalOrderItemRequest { ProductId = zero.Id, Quantity = 1 }] },
            Ct));
    }

    [Fact]
    public async Task Activate_is_refused_with_the_missing_codes()
    {
        await using var h = await CatalogHarness.CreateAsync();
        await EnableRentalsAsync(h);
        var product = await AddProductAsync(
            h, "Item", price: 0m, usesSchedule: true,
            visibility: CatalogProductVisibility.Restricted, isActive: false);

        var ex = await Assert.ThrowsAsync<ProductNotReadyException>(() => h.Products.ActivateAsync(product.Id, Ct));

        Assert.Equal(
            [CatalogProductReadiness.Resource, CatalogProductReadiness.Audience, CatalogProductReadiness.Price],
            ex.Missing);
        h.Db.ChangeTracker.Clear();
        Assert.False((await h.Products.GetByIdAsync(product.Id, Ct))!.IsActive);
    }

    [Fact]
    public async Task Activate_is_allowed_when_ready_and_for_sob_consulta()
    {
        await using var h = await CatalogHarness.CreateAsync();
        await EnableRentalsAsync(h);
        var scheduled = await AddProductAsync(h, "Agenda", usesSchedule: true, isActive: false);
        await AddRequirementAsync(h, scheduled.Id, Guid.NewGuid());
        var onRequest = await AddProductAsync(h, "Sob consulta", price: null, isActive: false);

        Assert.True((await h.Products.ActivateAsync(scheduled.Id, Ct))!.IsActive);
        Assert.True((await h.Products.ActivateAsync(onRequest.Id, Ct))!.IsActive);
    }

    [Fact]
    public async Task Activate_does_not_require_a_resource_when_rentals_module_is_off()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await AddProductAsync(h, "Agenda", usesSchedule: true, isActive: false);

        Assert.True((await h.Products.ActivateAsync(product.Id, Ct))!.IsActive);
    }

    [Fact]
    public async Task Deactivate_is_unchanged_for_a_product_that_is_not_ready()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await AddProductAsync(h, "Zero", price: 0m);

        Assert.False((await h.Products.DeactivateAsync(product.Id, Ct))!.IsActive);
    }

    [Fact]
    public async Task Create_as_active_still_works_but_the_product_is_hidden_and_reported_not_ready()
    {
        await using var h = await CatalogHarness.CreateAsync();
        await EnableRentalsAsync(h);

        var created = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 5m, UsesSchedule = true }, Ct);
        Assert.True(created.IsActive);
        Assert.False(created.SaleReadiness.Ready);
        Assert.Equal([CatalogProductReadiness.Resource], created.SaleReadiness.Missing);
        Assert.DoesNotContain(created.Id, await PortalIdsAsync(h));

        var inactive = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Rascunho", UsesSchedule = true, Status = "Inactive" }, Ct);
        Assert.False(inactive.IsActive);
        Assert.False(inactive.SaleReadiness.Ready);
        Assert.Equal([CatalogProductReadiness.Resource], inactive.SaleReadiness.Missing);
    }

    [Fact]
    public async Task Create_as_active_with_a_zero_price_is_reported_not_ready()
    {
        await using var h = await CatalogHarness.CreateAsync();

        var created = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 0m, UsesSchedule = false }, Ct);

        Assert.True(created.IsActive);
        Assert.Equal([CatalogProductReadiness.Price], created.SaleReadiness.Missing);
    }

    [Fact]
    public async Task Existing_active_product_that_is_not_ready_stays_active_but_hidden_from_the_portal()
    {
        await using var h = await CatalogHarness.CreateAsync();
        await EnableRentalsAsync(h);
        var product = await AddProductAsync(h, "Legado", usesSchedule: true);

        Assert.DoesNotContain(product.Id, await PortalIdsAsync(h));
        h.Db.ChangeTracker.Clear();
        var staff = (await h.Products.GetByIdAsync(product.Id, Ct))!;
        Assert.True(staff.IsActive);
        Assert.False(staff.SaleReadiness.Ready);
        Assert.Equal([CatalogProductReadiness.Resource], staff.SaleReadiness.Missing);
        var listed = (await h.Products.ListAsync(new CatalogProductListQuery(null, null, null), Ct)).Single();
        Assert.False(listed.SaleReadiness.Ready);
    }

    [Fact]
    public async Task Staff_readiness_is_ready_for_a_complete_product()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await AddProductAsync(h, "Completo");

        var dto = (await h.Products.GetByIdAsync(product.Id, Ct))!;

        Assert.True(dto.SaleReadiness.Ready);
        Assert.Empty(dto.SaleReadiness.Missing);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, true)]
    public async Task Predicate_and_per_product_check_agree(
        bool usesSchedule, bool restricted, bool hasResource, bool hasGrant)
    {
        foreach (var rentalsActive in new[] { false, true })
        foreach (var price in new decimal?[] { null, 0m, 10m })
        {
            await using var h = await CatalogHarness.CreateAsync();
            if (rentalsActive) await EnableRentalsAsync(h);
            var customer = h.AddCustomer();
            await h.Db.SaveChangesAsync();
            var product = await AddProductAsync(
                h, "Item", price, usesSchedule,
                restricted ? CatalogProductVisibility.Restricted : CatalogProductVisibility.Public);
            if (hasResource) await AddRequirementAsync(h, product.Id, Guid.NewGuid());
            if (hasGrant)
            {
                h.Db.CatalogProductAccess.Add(new CatalogProductAccess
                {
                    TenantId = h.Tenant.Id,
                    ProductId = product.Id,
                    CustomerId = customer.Id,
                });
                await h.Db.SaveChangesAsync();
            }

            var missing = (await CatalogProductReadiness.MissingForAsync(h.Db, h.Tenant.Id, [product], Ct))[product.Id];
            var sellable = h.Db.CatalogProducts
                .Where(CatalogProductReadiness.IsSellable(h.Db, h.Tenant.Id, rentalsActive))
                .Any(p => p.Id == product.Id);

            Assert.Equal(missing.Count == 0, sellable);
        }
    }
}
