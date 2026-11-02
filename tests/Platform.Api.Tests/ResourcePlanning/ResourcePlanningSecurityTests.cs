using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.ResourcePlanning;

public sealed partial class ResourcePlanningConcurrencyTests
{
    [DockerFact]
    public async Task Approve_with_foreign_or_mismatched_unit_returns_404_without_allocating()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        var foreignTenant = new FakeTenantProvider();
        var foreign = await SeedAsync(factory, foreignTenant);
        await using var db = factory.Create(tenant);
        var otherUnit = new Unit(seed.TenantId, "Other unit");
        db.Units.Add(otherUnit);
        await db.SaveChangesAsync();
        foreach (var unitId in new[] { foreign.UnitId, otherUnit.Id })
        {
            await using var attemptDb = factory.Create(tenant);
            var result = await SecurityController(attemptDb, tenant).Approve(seed.OrderId,
                new() { UnitId = unitId, WorkStart = WorkStart, ExpectedVersion = 0 }, default);
            Assert.IsType<NotFoundObjectResult>(result.Result);
        }
        Assert.False(await db.CatalogOrderItemResourceAllocations.AnyAsync());
        Assert.Equal(CatalogOrderStatus.Requested, (await db.CatalogOrders.SingleAsync()).Status);
    }

    [DockerFact]
    public async Task Planning_transitions_and_preview_require_allocate_and_schedule_read_permissions()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        await using var db = factory.Create(tenant);
        var authorization = new SecurityAuthorization();
        var controller = SecurityController(db, tenant, authorization);
        Assert.IsType<ForbidResult>((await controller.Approve(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart }, default)).Result);
        Assert.IsType<ForbidResult>((await controller.Preparing(seed.OrderId, default)).Result);
        Assert.IsType<ForbidResult>((await controller.AllocationPreview(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStarts = [] }, default)).Result);
        Assert.Equal(new[]
        {
            PermissionPolicies.Name(Permissions.Catalog.OrdersAllocate),
            PermissionPolicies.Name(Permissions.Catalog.OrdersAllocate),
            PermissionPolicies.Name(Permissions.Rentals.ScheduleRead),
        }, authorization.RequestedPolicies);
        Assert.False(await db.CatalogOrderItemResourceAllocations.AnyAsync());
    }

    [DockerFact]
    public async Task Multiple_requirements_return_409_on_approve_and_400_on_configuration()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        await using var db = factory.Create(tenant);
        var original = await db.CatalogOrderItemResourceRequirements.SingleAsync();
        db.CatalogOrderItemResourceRequirements.Add(new()
        {
            TenantId = seed.TenantId, OrderItemId = original.OrderItemId,
            RentalAssetId = seed.RentalAssetId, ResourceQuantity = 1, DurationMinutesPerUnit = 30,
        });
        await db.SaveChangesAsync();
        var approval = await SecurityController(db, tenant).Approve(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart }, default);
        AssertCode(Assert.IsType<ConflictObjectResult>(approval.Result), "unsupported_requirement_scope");
        var products = new CatalogProductsController(null!, new CatalogProductResourceRequirementService(db, tenant));
        var requirement = new CatalogProductResourceRequirementDto
        {
            RentalAssetId = seed.RentalAssetId, ResourceQuantity = 1, DurationMinutesPerUnit = 60,
        };
        var config = await products.ReplaceResourceRequirements(seed.ProductId,
            new() { ExpectedVersion = 0, Requirements = [requirement, requirement] }, default);
        AssertCode(Assert.IsType<BadRequestObjectResult>(config.Result), "unsupported_requirement_scope");
        Assert.False(await db.CatalogOrderItemResourceAllocations.AnyAsync());
    }

    [DockerFact]
    public async Task Concurrent_requirement_replacements_return_exactly_one_409_version_conflict()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        await using var db1 = factory.Create(tenant);
        await using var db2 = factory.Create(tenant);
        var request = new ReplaceCatalogProductResourceRequirementsRequest
        {
            ExpectedVersion = 0,
            Requirements = [new() { RentalAssetId = seed.RentalAssetId, ResourceQuantity = 1, DurationMinutesPerUnit = 60 }],
        };
        var first = new CatalogProductsController(null!, new CatalogProductResourceRequirementService(db1, tenant));
        var second = new CatalogProductsController(null!, new CatalogProductResourceRequirementService(db2, tenant));
        var results = await Task.WhenAll(
            first.ReplaceResourceRequirements(seed.ProductId, request, default),
            second.ReplaceResourceRequirements(seed.ProductId, request, default));
        Assert.Single(results, r => r.Result is OkObjectResult);
        AssertCode(Assert.IsType<ConflictObjectResult>(Assert.Single(results, r => r.Result is ConflictObjectResult).Result),
            "planning_version_conflict");
        await using var verify = factory.Create(tenant);
        Assert.Single(await verify.CatalogProductResourceRequirements.Where(r => r.IsActive).ToListAsync());
        Assert.Equal(1, (await verify.CatalogProducts.SingleAsync()).ResourceRequirementsVersion);
    }

    [DockerFact]
    public async Task Concurrent_cancel_and_start_have_one_winner_and_never_cancel_running_occupancy()
    {
        await AssertCancelStartRaceAsync(standaloneUsage: false);
        await AssertCancelStartRaceAsync(standaloneUsage: true);
    }

    private async Task AssertCancelStartRaceAsync(bool standaloneUsage)
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        await using (var setup = factory.Create(tenant))
        {
            await CreateOrders(setup, tenant).ApproveAsync(seed.OrderId,
                new() { UnitId = seed.UnitId, WorkStart = WorkStart }, default);
        }
        await using var cancelDb = factory.Create(tenant);
        await using var startDb = factory.Create(tenant);
        // Both callers have read the same revision before either can write.
        await cancelDb.CatalogOrders.Include(o => o.Items).Include(o => o.History).SingleAsync();
        await startDb.CatalogOrders.Include(o => o.Items).Include(o => o.History).SingleAsync();
        await cancelDb.CatalogOrderItemResourceAllocations.SingleAsync();
        var startAllocation = await startDb.CatalogOrderItemResourceAllocations.SingleAsync();
        var results = await Task.WhenAll(
            CaptureAsync(() => CreateOrders(cancelDb, tenant).CancelAsync(seed.OrderId, "cancel", default)),
            CaptureAsync(() => standaloneUsage
                ? new CatalogOrderAllocationUsageService(startDb, tenant, new ResourceOccupancyService())
                    .ApplyUsageActionAsync(seed.OrderId, startAllocation.Id,
                        new() { Action = "start", ExpectedVersion = startAllocation.PlanningRevision }, default)
                : CreateOrders(startDb, tenant).StartPreparingAsync(seed.OrderId, default)));
        Assert.Single(results, r => r.Error is null);
        Assert.IsType<InvalidCatalogOrderTransitionException>(Assert.Single(results, r => r.Error is not null).Error);
        await using var verify = factory.Create(tenant);
        var allocation = await verify.CatalogOrderItemResourceAllocations.SingleAsync();
        var reservation = await verify.Reservations.SingleAsync();
        if (results[1].Error is null)
        {
            Assert.Equal(AllocationStatus.Running, allocation.Status);
            Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
            Assert.Null(allocation.ActualReleaseAt);
        }
        else
        {
            Assert.Equal(AllocationStatus.Cancelled, allocation.Status);
            Assert.Null(allocation.ActualStartAt);
        }
    }

    [DockerFact]
    public async Task Invalid_duration_quantity_and_date_ranges_return_400_on_approve_and_preview()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        await using var db = factory.Create(tenant);
        var requirement = await db.CatalogOrderItemResourceRequirements.SingleAsync();
        var item = await db.CatalogOrderItems.SingleAsync();
        foreach (var (duration, quantity, start) in new[]
        {
            (int.MaxValue, 2, WorkStart), (60, int.MaxValue, WorkStart),
            (1441, 1, WorkStart), (1, 1441, WorkStart),
            (0, 1, WorkStart), (60, 0, WorkStart), (60, 1, DateTimeOffset.MaxValue),
        })
        {
            requirement.DurationMinutesPerUnit = duration;
            item.Quantity = quantity;
            await db.SaveChangesAsync();
            await using var attempt = factory.Create(tenant);
            var controller = SecurityController(attempt, tenant);
            Assert.IsType<BadRequestObjectResult>((await controller.Approve(seed.OrderId,
                new() { UnitId = seed.UnitId, WorkStart = start }, default)).Result);
            Assert.IsType<BadRequestObjectResult>((await controller.AllocationPreview(seed.OrderId,
                new() { UnitId = seed.UnitId, WorkStarts = [new() { OrderItemId = item.Id, WorkStart = start }] }, default)).Result);
        }
        Assert.False(await db.CatalogOrderItemResourceAllocations.AnyAsync());
        var products = new CatalogProductsController(null!, new CatalogProductResourceRequirementService(db, tenant));
        Assert.IsType<BadRequestObjectResult>((await products.ReplaceResourceRequirements(seed.ProductId,
            new() { ExpectedVersion = 0, Requirements = [new()
            {
                RentalAssetId = seed.RentalAssetId, ResourceQuantity = 1, DurationMinutesPerUnit = int.MaxValue,
            }] }, default)).Result);
    }

    [DockerFact]
    public async Task Migration_down_rejects_internal_use_before_destructive_operations()
    {
        var factory = RequireFactory();
        var tenant = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenant);
        await using var db = factory.Create(tenant);
        await CreateOrders(db, tenant).ApproveAsync(seed.OrderId,
            new() { UnitId = seed.UnitId, WorkStart = WorkStart }, default);
        var migration = new AddResourcePlanningPhase0();
        var guard = Assert.IsType<SqlOperation>(migration.DownOperations[0]);
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(guard.Sql));
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Contains("Cannot roll back resource planning", error.MessageText);
        Assert.Null((await db.Reservations.AsNoTracking().SingleAsync()).CustomerId);
        Assert.Single(await db.CatalogOrderItemResourceAllocations.AsNoTracking().ToListAsync());
    }

    private static CatalogOrdersController SecurityController(AppDbContext db, FakeTenantProvider tenant,
        SecurityAuthorization? authorization = null)
    {
        var occupancy = new ResourceOccupancyService();
        var publisher = new CatalogNotificationPublisher(db, tenant, new FakePermissionResolver(),
            NullLogger<CatalogNotificationPublisher>.Instance);
        return new(CreateOrders(db, tenant),
            new CatalogOrderAllocationService(db, tenant, occupancy, publisher.AsDomainEvents(), new RecordingOutboxScheduler()),
            new CatalogOrderAllocationPreviewService(db, tenant, occupancy),
            new CatalogOrderAllocationUsageService(db, tenant, occupancy), authorization ?? new SecurityAuthorization(true));
    }

    private static void AssertCode(ObjectResult result, string expected) =>
        Assert.Equal(expected, JsonSerializer.SerializeToElement(result.Value).GetProperty("code").GetString());

    private sealed class SecurityAuthorization(bool allow = false) : IAuthorizationService
    {
        public List<string> RequestedPolicies { get; } = [];
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            RequestedPolicies.Add(policyName);
            return Task.FromResult(allow ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) => throw new NotSupportedException();
    }
}
