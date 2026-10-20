using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogPolicyTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Missing_row_preserves_defaults_without_creating_policy()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var service = new CatalogPolicyService(h.Db, h.TenantProvider);
        Assert.Equal(new CatalogPolicyResponse(true, CatalogOrderStatus.Requested, false,
            ProductRequestStatus.Submitted, null), await service.GetAsync(Ct));
        Assert.Empty(h.Db.CatalogPolicies);
        var (customer, order) = await SeedOrder(h, CatalogOrderStatus.Requested);
        Assert.Equal(CatalogOrderStatus.Cancelled, (await h.Portal.CancelOrderAsync(customer.Id, order.Id, Ct)).Status);
        var request = await h.Portal.CreateProductRequestAsync(customer.Id,
            new CreateProductRequestDto { Description = "Item", Quantity = 1 }, [], Ct);
        await Assert.ThrowsAsync<InvalidCatalogOrderTransitionException>(() =>
            h.Portal.CancelProductRequestAsync(customer.Id, request.Id, Ct));
        Assert.Empty(h.Db.CatalogPolicies);
    }

    [Theory]
    [InlineData(CatalogOrderStatus.Requested, CatalogOrderStatus.Requested, true)]
    [InlineData(CatalogOrderStatus.Approved, CatalogOrderStatus.Approved, true)]
    [InlineData(CatalogOrderStatus.Preparing, CatalogOrderStatus.Preparing, true)]
    [InlineData(CatalogOrderStatus.Ready, CatalogOrderStatus.Ready, true)]
    [InlineData(CatalogOrderStatus.Requested, CatalogOrderStatus.Ready, true)]
    [InlineData(CatalogOrderStatus.Approved, CatalogOrderStatus.Requested, false)]
    [InlineData(CatalogOrderStatus.Preparing, CatalogOrderStatus.Approved, false)]
    [InlineData(CatalogOrderStatus.Ready, CatalogOrderStatus.Preparing, false)]
    [InlineData(CatalogOrderStatus.Completed, CatalogOrderStatus.Ready, false)]
    [InlineData(CatalogOrderStatus.Rejected, CatalogOrderStatus.Ready, false)]
    [InlineData(CatalogOrderStatus.Cancelled, CatalogOrderStatus.Ready, false)]
    public async Task Order_policy_enforces_boundary_and_terminal_states(
        CatalogOrderStatus status, CatalogOrderStatus limit, bool allowed)
    {
        await using var h = await CatalogHarness.CreateAsync();
        var (customer, order) = await SeedOrder(h, status);
        await Policy(h).UpsertAsync(Input(orderLimit: limit), Ct);
        if (allowed)
        {
            var result = await h.Portal.CancelOrderAsync(customer.Id, order.Id, Ct);
            Assert.Equal(CatalogOrderStatus.Cancelled, result.Status);
            var history = result.History.Last();
            Assert.Equal(CatalogActorType.Customer, history.ActorType);
            Assert.Equal(customer.Id, history.ActorId);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidCatalogOrderTransitionException>(() =>
                h.Portal.CancelOrderAsync(customer.Id, order.Id, Ct));
            Assert.Equal(status, order.Status);
        }
    }

    [Fact]
    public async Task Disabled_orders_are_rejected_but_staff_cancel_is_unchanged()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var (customer, order) = await SeedOrder(h, CatalogOrderStatus.Requested);
        await Policy(h).UpsertAsync(Input(orders: false), Ct);
        await Assert.ThrowsAsync<InvalidCatalogOrderTransitionException>(() =>
            h.Portal.CancelOrderAsync(customer.Id, order.Id, Ct));
        var result = await h.Orders.CancelAsync(order.Id, "Staff reason", Ct);
        Assert.Equal(CatalogOrderStatus.Cancelled, result.Status);
    }

    [Theory]
    [InlineData(false, ProductRequestStatus.Submitted, false)]
    [InlineData(true, ProductRequestStatus.Submitted, true)]
    [InlineData(true, ProductRequestStatus.Cancelled, false)]
    [InlineData(true, (ProductRequestStatus)123, false)]
    public async Task Request_policy_enforces_flag_and_status(bool enabled, ProductRequestStatus status, bool allowed)
    {
        await using var h = await CatalogHarness.CreateAsync();
        var customer = h.AddCustomer();
        var request = new ProductRequest { TenantId = h.Tenant.Id, CustomerId = customer.Id,
            Description = "Item", Quantity = 1, Status = status };
        h.Db.ProductRequests.Add(request);
        await h.Db.SaveChangesAsync();
        await Policy(h).UpsertAsync(Input(requests: enabled), Ct);
        if (allowed)
        {
            Assert.Equal(ProductRequestStatus.Cancelled,
                (await h.Portal.CancelProductRequestAsync(customer.Id, request.Id, Ct)).Status);
            h.Db.ChangeTracker.Clear();
            Assert.Equal(ProductRequestStatus.Cancelled, (await h.Db.ProductRequests.SingleAsync()).Status);
        }
        else
            await Assert.ThrowsAsync<InvalidCatalogOrderTransitionException>(() =>
                h.Portal.CancelProductRequestAsync(customer.Id, request.Id, Ct));
    }

    [Fact]
    public async Task Policies_and_cancellation_are_tenant_scoped_and_owner_scoped()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var (owner, order) = await SeedOrder(h, CatalogOrderStatus.Requested);
        var other = h.AddCustomer("Other");
        var request = new ProductRequest { TenantId = h.Tenant.Id, CustomerId = owner.Id,
            Description = "Item", Quantity = 1 };
        h.Db.ProductRequests.Add(request);
        await h.Db.SaveChangesAsync();
        await Policy(h).UpsertAsync(Input(orders: false, requests: true, text: "Tenant A"), Ct);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Portal.CancelOrderAsync(other.Id, order.Id, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Portal.CancelProductRequestAsync(other.Id, request.Id, Ct));
        h.TenantProvider.TenantId = Guid.NewGuid();
        Assert.Null((await Policy(h).GetAsync(Ct)).CancellationPolicyText);
        Assert.True((await Policy(h).GetAsync(Ct)).CustomerCanCancelOrders);
        await Policy(h).UpsertAsync(Input(text: "Tenant B"), Ct);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Portal.CancelOrderAsync(owner.Id, order.Id, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Portal.CancelProductRequestAsync(owner.Id, request.Id, Ct));
        h.TenantProvider.TenantId = h.Tenant.Id;
        Assert.Equal("Tenant A", (await Policy(h).GetAsync(Ct)).CancellationPolicyText);
        Assert.False((await Policy(h).GetAsync(Ct)).CustomerCanCancelOrders);
        Assert.Single(h.Db.CatalogPolicies);
        Assert.Equal(2, await h.Db.CatalogPolicies.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Put_replaces_single_row_and_validates_text_and_statuses()
    {
        await using var h = await CatalogHarness.CreateAsync();
        await Policy(h).UpsertAsync(Input(text: new string('x', 2000)), Ct);
        var result = await Policy(h).UpsertAsync(Input(orders: false), Ct);
        Assert.Null(result.CancellationPolicyText);
        Assert.False(result.CustomerCanCancelOrders);
        Assert.Single(h.Db.CatalogPolicies);
        await Assert.ThrowsAsync<ArgumentException>(() => Policy(h).UpsertAsync(Input(text: new string('x', 2001)), Ct));
        foreach (var status in new[] { CatalogOrderStatus.Completed, CatalogOrderStatus.Cancelled,
            CatalogOrderStatus.Rejected, (CatalogOrderStatus)(-1), (CatalogOrderStatus)100 })
            await Assert.ThrowsAsync<ArgumentException>(() => Policy(h).UpsertAsync(Input(orderLimit: status), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Policy(h).UpsertAsync(new UpsertCatalogPolicyRequest
        { RequestCancelableUpToStatus = ProductRequestStatus.Cancelled }, Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Missing_tenant_fails_closed(string? tenant)
    {
        await using var h = await CatalogHarness.CreateAsync();
        h.TenantProvider.TenantId = tenant is null ? null : Guid.Parse(tenant);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Policy(h).GetAsync(Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Policy(h).UpsertAsync(Input(), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Portal.CancelOrderAsync(Guid.NewGuid(), Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Portal.CancelProductRequestAsync(Guid.NewGuid(), Guid.NewGuid(), Ct));
    }

    [Fact]
    public void Admin_endpoints_require_catalog_manage_and_portal_read_requires_customer()
    {
        foreach (var name in new[] { "Get", "Put" })
            Assert.Equal(PermissionPolicies.Name(Permissions.Catalog.ProductsManage),
                typeof(CatalogPolicyController).GetMethod(name)!.GetCustomAttribute<RequirePermissionAttribute>()!.Policy);
        var portal = typeof(CatalogPolicyController).GetMethod("GetPortal")!;
        Assert.Null(portal.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        Assert.Equal("Customer", portal.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void Json_contract_uses_exact_fields_and_requires_put_flags_and_limits()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var portal = new PortalCatalogPolicyResponse(true, false,
            new(CatalogOrderStatus.Requested, ProductRequestStatus.Submitted), null);
        Assert.Equal("""{"canCancelOrders":true,"canCancelRequests":false,"upToStatus":{"orders":"Requested","requests":"Submitted"},"policyText":null}""",
            JsonSerializer.Serialize(portal, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertCatalogPolicyRequest>("{}", options));
    }

    [Fact]
    public async Task Stale_request_cancellation_detects_concurrent_state_change()
    {
        var provider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        var databaseName = $"request-concurrency-{Guid.NewGuid():N}";
        await using var db1 = InMemoryAppDb.Create(provider, databaseName);
        await using var db2 = InMemoryAppDb.Create(provider, databaseName);
        var request = new ProductRequest { TenantId = provider.TenantId.Value, CustomerId = Guid.NewGuid(),
            Description = "Item", Quantity = 1 };
        db1.ProductRequests.Add(request);
        await db1.SaveChangesAsync();
        var stale = await db2.ProductRequests.SingleAsync();
        request.Status = ProductRequestStatus.Cancelled;
        await db1.SaveChangesAsync();
        stale.Status = ProductRequestStatus.Cancelled;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }

    private static CatalogPolicyService Policy(CatalogHarness h) => new(h.Db, h.TenantProvider);
    private static UpsertCatalogPolicyRequest Input(bool orders = true, bool requests = false,
        CatalogOrderStatus orderLimit = CatalogOrderStatus.Requested, string? text = null) => new()
    {
        CustomerCanCancelOrders = orders, CustomerCanCancelRequests = requests,
        OrderCancelableUpToStatus = orderLimit, RequestCancelableUpToStatus = ProductRequestStatus.Submitted,
        CancellationPolicyText = text,
    };

    private static async Task<(Customer, CatalogOrder)> SeedOrder(CatalogHarness h, CatalogOrderStatus status)
    {
        var customer = h.AddCustomer();
        var order = new CatalogOrder { TenantId = h.Tenant.Id, CustomerId = customer.Id,
            CustomerNameSnapshot = customer.Name, OrderNumber = 1 };
        if (status == CatalogOrderStatus.Rejected) order.Reject("Reason", CatalogActorType.B2BUser, null);
        else if (status == CatalogOrderStatus.Cancelled) order.Cancel(CatalogActorType.Customer, null, customer.Id);
        else
        {
            if (status >= CatalogOrderStatus.Approved) order.Approve(CatalogActorType.B2BUser, null);
            if (status >= CatalogOrderStatus.Preparing) order.StartPreparing(CatalogActorType.B2BUser, null);
            if (status >= CatalogOrderStatus.Ready) order.MarkReady(CatalogActorType.B2BUser, null);
            if (status == CatalogOrderStatus.Completed) order.Complete(CatalogActorType.B2BUser, null);
        }
        h.Db.CatalogOrders.Add(order);
        await h.Db.SaveChangesAsync();
        return (customer, order);
    }
}
