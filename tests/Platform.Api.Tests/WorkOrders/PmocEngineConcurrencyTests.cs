using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.WorkOrders;

public sealed class PmocEngineConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private const int RaceIterations = 8;
    private static readonly DateOnly AsOf = new(2026, 9, 19);
    private static readonly DateOnly StaleDue = new(2026, 9, 1);

    private readonly PostgresContainerFixture _postgres;

    public PmocEngineConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task P_two_automatic_generators_create_at_most_one_work_order()
    {
        var factory = RequireFactory();
        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var tenantProvider = new FakeTenantProvider();
            var seed = await SeedDueAssetAsync(factory, tenantProvider);

            await using var db1 = factory.Create(tenantProvider);
            await using var db2 = factory.Create(tenantProvider);
            var first = CreateGenerator(db1, tenantProvider).TryGenerateAutomaticAsync(
                seed.PlanId,
                seed.AssetId,
                AsOf,
                CancellationToken.None);
            var second = CreateGenerator(db2, tenantProvider).TryGenerateAutomaticAsync(
                seed.PlanId,
                seed.AssetId,
                AsOf,
                CancellationToken.None);

            var results = await Task.WhenAll(first, second);

            await using var verify = factory.Create(tenantProvider);
            var created = await verify.WorkOrders.CountAsync(workOrder =>
                workOrder.MaintenancePlanId == seed.PlanId
                && workOrder.Status != WorkOrderStatus.Canceled);
            Assert.Equal(1, created);
            Assert.Equal(1, results.Count(result => result.Outcome == PmocAutomaticGenerationOutcome.Created));
            Assert.Contains(
                results,
                result => result.Outcome is PmocAutomaticGenerationOutcome.SkippedOpenWorkOrder
                    or PmocAutomaticGenerationOutcome.SkippedDuplicate);
        }
    }

    [DockerFact]
    public async Task AH_completion_does_not_leave_a_stale_automatic_work_order()
    {
        var factory = RequireFactory();
        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var tenantProvider = new FakeTenantProvider();
            var seed = await SeedDueAssetAsync(factory, tenantProvider);

            await using var generationDb = factory.Create(tenantProvider);
            await using var completionDb = factory.Create(tenantProvider);
            var generate = CreateGenerator(generationDb, tenantProvider).TryGenerateAutomaticAsync(
                seed.PlanId,
                seed.AssetId,
                AsOf,
                CancellationToken.None);
            var complete = CompleteManualAsync(completionDb, tenantProvider, seed.PlanId, seed.AssetId);

            await Task.WhenAll(generate, complete);

            await using var verify = factory.Create(tenantProvider);
            var rows = await verify.WorkOrders
                .Where(workOrder => workOrder.MaintenancePlanId == seed.PlanId)
                .ToListAsync();
            var completed = Assert.Single(rows, workOrder => workOrder.Status == WorkOrderStatus.Completed);
            Assert.NotNull(completed.CompletedDate);
            Assert.DoesNotContain(
                rows,
                workOrder =>
                    workOrder.ScheduledDate == StaleDue
                    && workOrder.CreatedAt > completed.CompletedDate);
        }
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory!;
    }

    private static async Task CompleteManualAsync(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Guid planId,
        Guid assetId)
    {
        var manual = await CreateGenerator(db, tenantProvider).GenerateAsync(
            new GenerateWorkOrderCommand(planId, assetId, StaleDue.AddDays(1), null),
            CancellationToken.None);
        var updated = await CreateStatusService(db, tenantProvider).UpdateStatusAsync(
            manual.Id,
            new UpdateWorkOrderStatusRequest { Status = WorkOrderStatus.Completed },
            CancellationToken.None);
        Assert.NotNull(updated);
        Assert.Equal(WorkOrderStatus.Completed, updated!.Status);
    }

    private static async Task<Seed> SeedDueAssetAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider)
    {
        await using var db = factory.Create(tenantProvider);
        var tenant = new Tenant(
            "Lock Club",
            Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
            subdomain: $"pmoc-{Guid.NewGuid():N}"[..16]);
        tenantProvider.TenantId = tenant.Id;
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "AC" };
        var family = new AssetFamily
        {
            Key = $"elec-{Guid.NewGuid():N}"[..32],
            Label = "Electrical",
            FieldSchemaJson = """{"fields":[]}""",
        };
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, family.Id));
        await db.SaveChangesAsync();

        var plan = new MaintenancePlan
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            Name = "Race plan",
            IntervalDays = 30,
            FirstDueDate = StaleDue,
            AssetCategoryId = category.Id,
            IsActive = true,
            AutoGenerateEnabled = true,
        };
        plan.AddTask(new PlanTask
        {
            TenantId = tenant.Id,
            MaintenancePlanId = plan.Id,
            Title = "Filtro",
            InputType = TaskInputType.Checkbox,
            IsMandatory = false,
            Order = 1,
        });
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Split",
            Tag = $"AC-{Guid.NewGuid():N}"[..12],
            Status = AssetStatus.Active,
        };
        db.MaintenancePlans.Add(plan);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return new Seed(plan.Id, asset.Id);
    }

    private static WorkOrderGenerationService CreateGenerator(AppDbContext db, FakeTenantProvider tenantProvider) =>
        new(db, tenantProvider, TestPermissionResolvers.Create(db, tenantProvider));

    private static WorkOrderService CreateStatusService(AppDbContext db, FakeTenantProvider tenantProvider)
    {
        var http = new FakeHttpContextAccessor();
        var identity = new ClaimsIdentity(
            [new Claim("email", "ops@club.test"), new Claim("sub", "admin-sub")],
            authenticationType: "Test");
        http.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var resolver = TestPermissionResolvers.Create(db, tenantProvider);
        var users = new UserDirectoryService(
            db,
            tenantProvider,
            new FakePlatformAdminChecker("ops@club.test"),
            resolver,
            new RbacGrantGuard(db, resolver, NullLogger<RbacGrantGuard>.Instance),
            new FakeTrialGuard(),
            new NotificationQueue(),
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(),
            NullLogger<UserDirectoryService>.Instance);
        var registry = new AssetRegistry(
            db,
            tenantProvider,
            new AssetService(db, tenantProvider, new FakeTrialGuard()),
            new AssetFamilyService(db));
        return new WorkOrderService(db, tenantProvider, http, users, resolver, registry);
    }

    private sealed record Seed(Guid PlanId, Guid AssetId);
}
