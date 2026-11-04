using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Standalone usage actions (fix/order-usage-history-ready): work-finished and release must each
/// record an order-history entry carrying a stable reason code and the resolved actor, and a
/// work-finished that leaves every current allocation finished must auto-advance a Preparing order
/// to Ready and publish OrderReady exactly once. One-scheduled-line-per-order (see
/// <see cref="CatalogOrderAllocationService"/>) means the "another allocation still Running" guard
/// is covered by never advancing when the single allocation is still Running.
/// </summary>
public sealed partial class ResourcePlanningConcurrencyTests
{
    [DockerFact]
    public async Task Work_finished_records_history_with_actor_and_auto_marks_ready()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        await using (var prep = factory.Create(tenant))
        {
            var orders = CreateOrders(prep, tenant);
            await orders.ApproveAsync(seed.OrderId,
                new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);
            await orders.StartPreparingAsync(seed.OrderId, CancellationToken.None);
        }

        var staffId = await AddStaffUserAsync(factory, tenant, seed, "sub-wf");

        await using (var act = factory.Create(tenant))
        {
            var (usage, scheduler) = CreateUsage(act, tenant, "sub-wf");
            var allocation = await act.CatalogOrderItemResourceAllocations.SingleAsync();
            var result = await usage.ApplyUsageActionAsync(seed.OrderId, allocation.Id,
                new() { Action = "work-finished", ExpectedVersion = allocation.PlanningRevision }, CancellationToken.None);
            Assert.Equal(CatalogOrderStatus.Ready, result.Status);
        }

        await using var verify = factory.Create(tenant);
        var order = await verify.CatalogOrders.Include(o => o.History).SingleAsync(o => o.Id == seed.OrderId);
        Assert.Equal(CatalogOrderStatus.Ready, order.Status);

        var workFinished = Assert.Single(order.History,
            h => h.Reason == CatalogOrderUsageHistoryReasons.WorkFinished);
        Assert.Equal(CatalogActorType.B2BUser, workFinished.ActorType);
        Assert.Equal(staffId, workFinished.ActorId);

        // Auto-Ready recorded a Ready entry and published OrderReady exactly once.
        Assert.Contains(order.History, h => h.Status == CatalogOrderStatus.Ready && h.Reason is null);
        Assert.Equal(1, await verify.Notifications.CountAsync(n =>
            n.EventType == CatalogEventTypes.OrderReady && n.AggregateId == seed.OrderId));
    }

    [DockerFact]
    public async Task Release_records_history_with_actor_without_marking_ready()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        await using (var prep = factory.Create(tenant))
        {
            var orders = CreateOrders(prep, tenant);
            await orders.ApproveAsync(seed.OrderId,
                new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);
            await orders.StartPreparingAsync(seed.OrderId, CancellationToken.None);
        }

        var staffId = await AddStaffUserAsync(factory, tenant, seed, "sub-rel");

        // Finish work (order auto-advances to Ready), then release the cleaning resource.
        await using (var act = factory.Create(tenant))
        {
            var (usage, _) = CreateUsage(act, tenant, "sub-rel");
            var running = await act.CatalogOrderItemResourceAllocations.SingleAsync();
            await usage.ApplyUsageActionAsync(seed.OrderId, running.Id,
                new() { Action = "work-finished", ExpectedVersion = running.PlanningRevision }, CancellationToken.None);
            var cleaning = await act.CatalogOrderItemResourceAllocations.SingleAsync();
            await usage.ApplyUsageActionAsync(seed.OrderId, cleaning.Id,
                new() { Action = "release", ExpectedVersion = cleaning.PlanningRevision }, CancellationToken.None);
        }

        await using var verify = factory.Create(tenant);
        var order = await verify.CatalogOrders.Include(o => o.History).SingleAsync(o => o.Id == seed.OrderId);
        var released = Assert.Single(order.History, h => h.Reason == CatalogOrderUsageHistoryReasons.Released);
        Assert.Equal(CatalogActorType.B2BUser, released.ActorType);
        Assert.Equal(staffId, released.ActorId);
        // Release does not itself transition the commercial status.
        Assert.Equal(CatalogOrderStatus.Ready, order.Status);
    }

    [DockerFact]
    public async Task Work_finished_does_not_mark_cancelled_order_ready()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);

        // Approve + prepare (allocation Running), then cancel: a Running allocation keeps occupying
        // the resource, so a later work-finished is still valid but must not revive the order.
        await using (var prep = factory.Create(tenant))
        {
            var orders = CreateOrders(prep, tenant);
            await orders.ApproveAsync(seed.OrderId,
                new() { UnitId = seed.UnitId, WorkStart = WorkStart, ExpectedVersion = 0 }, CancellationToken.None);
            await orders.StartPreparingAsync(seed.OrderId, CancellationToken.None);
            await orders.CancelAsync(seed.OrderId, "stopped", CancellationToken.None);
        }

        await using (var act = factory.Create(tenant))
        {
            var (usage, _) = CreateUsage(act, tenant, httpSub: null);
            var allocation = await act.CatalogOrderItemResourceAllocations.SingleAsync();
            var result = await usage.ApplyUsageActionAsync(seed.OrderId, allocation.Id,
                new() { Action = "work-finished", ExpectedVersion = allocation.PlanningRevision }, CancellationToken.None);
            Assert.Equal(CatalogOrderStatus.Cancelled, result.Status);
        }

        await using var verify = factory.Create(tenant);
        var order = await verify.CatalogOrders.Include(o => o.History).SingleAsync(o => o.Id == seed.OrderId);
        Assert.Equal(CatalogOrderStatus.Cancelled, order.Status);
        Assert.DoesNotContain(order.History, h => h.Status == CatalogOrderStatus.Ready);
        Assert.Equal(0, await verify.Notifications.CountAsync(n =>
            n.EventType == CatalogEventTypes.OrderReady && n.AggregateId == seed.OrderId));
        // No-actor (System) resolution still records the usage entry.
        var workFinished = Assert.Single(order.History,
            h => h.Reason == CatalogOrderUsageHistoryReasons.WorkFinished);
        Assert.Equal(CatalogActorType.System, workFinished.ActorType);
        Assert.Null(workFinished.ActorId);
    }

    private static async Task<Guid> AddStaffUserAsync(
        PostgresAppDbFactory factory, FakeTenantProvider tenant, Seed seed, string sub)
    {
        await using var db = factory.Create(tenant);
        var user = new User(seed.TenantId, sub, "Operador", $"{Guid.NewGuid():N}@ops.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static (CatalogOrderAllocationUsageService Usage, RecordingOutboxScheduler Scheduler) CreateUsage(
        AppDbContext db, FakeTenantProvider tenant, string? httpSub)
    {
        var publisher = new CatalogNotificationPublisher(
            db, tenant, new FakePermissionResolver(), NullLogger<CatalogNotificationPublisher>.Instance);
        var scheduler = new RecordingOutboxScheduler();
        var http = new FakeHttpContextAccessor();
        if (httpSub is not null)
        {
            http.HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", httpSub)], "Test")),
            };
        }

        var usage = new CatalogOrderAllocationUsageService(
            db, tenant, new ResourceOccupancyService(BookingClock()), http, publisher.AsDomainEvents(), scheduler);
        return (usage, scheduler);
    }
}
