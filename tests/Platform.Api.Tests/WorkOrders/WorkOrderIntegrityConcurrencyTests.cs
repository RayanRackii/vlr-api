using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderIntegrityConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public WorkOrderIntegrityConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Task_value_waits_on_the_row_lock_and_rejects_a_committed_completion()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        await using var holder = factory.Create(tenantProvider);
        await using var writer = factory.Create(tenantProvider);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT id
            FROM os.work_orders
            WHERE id = {seed.WorkOrderId}
              AND tenant_id = {seed.TenantId}
            FOR UPDATE
            """);
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE os.work_orders
            SET status = 'Completed', completed_date = {DateTimeOffset.Parse("2026-10-17T12:00:00Z")}
            WHERE id = {seed.WorkOrderId}
            """);

        await writer.Database.OpenConnectionAsync();
        var writerConnection = writer.Database.GetDbConnection();
        await using var pidCommand = writerConnection.CreateCommand();
        pidCommand.CommandText = "SELECT pg_backend_pid()";
        var backendPid = Convert.ToInt32(await pidCommand.ExecuteScalarAsync());

        var update = CreateService(writer, tenantProvider).UpdateTaskValueAsync(
            seed.WorkOrderId,
            seed.TaskId,
            new UpdateWorkOrderTaskValueRequest { Value = "queued" },
            CancellationToken.None);

        var committed = false;
        try
        {
            var waiting = await WaitForPostgresLockWaitAsync(factory, tenantProvider, backendPid);
            Assert.True(waiting, "The task-value write did not wait on the work-order row lock.");
            Assert.False(
                update.IsCompleted,
                "The task-value write finished before the row lock was released.");

            await transaction.CommitAsync();
            committed = true;

            var rejected = await Assert.ThrowsAsync<WorkOrderClosedException>(() => update);
            Assert.Equal(WorkOrderErrorCodes.Closed, rejected.Code);

            await using var verify = factory.Create(tenantProvider);
            var value = await verify.WorkOrderTasks
                .Where(task => task.Id == seed.TaskId)
                .Select(task => task.Value)
                .SingleAsync();
            Assert.Null(value);
        }
        finally
        {
            if (!committed)
            {
                await transaction.RollbackAsync();
                try
                {
                    await update.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Keep the assertion that failed while the writer was blocked.
                }
            }
        }
    }

    [DockerFact]
    public async Task Completion_and_task_value_serialize_so_a_late_value_cannot_land_on_a_closed_order()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        await using var statusDb = factory.Create(tenantProvider);
        await using var valueDb = factory.Create(tenantProvider);
        var status = CreateService(statusDb, tenantProvider).UpdateStatusAsync(
            seed.WorkOrderId,
            new UpdateWorkOrderStatusRequest { Status = WorkOrderStatus.Completed },
            CancellationToken.None);
        var value = CreateService(valueDb, tenantProvider).UpdateTaskValueAsync(
            seed.WorkOrderId,
            seed.TaskId,
            new UpdateWorkOrderTaskValueRequest { Value = "queued" },
            CancellationToken.None);

        var statusError = await ObserveAsync(status);
        var valueError = await ObserveAsync(value);

        await using var verify = factory.Create(tenantProvider);
        var stored = await verify.WorkOrders
            .Include(workOrder => workOrder.Tasks)
            .SingleAsync(workOrder => workOrder.Id == seed.WorkOrderId);

        Assert.Equal(WorkOrderStatus.Completed, stored.Status);
        Assert.Null(statusError);

        var taskValue = stored.Tasks.Single().Value;
        if (valueError is null)
        {
            Assert.Equal("queued", taskValue);
            return;
        }

        var closed = Assert.IsType<WorkOrderClosedException>(valueError);
        Assert.Equal(WorkOrderErrorCodes.Closed, closed.Code);
        Assert.Null(taskValue);
    }

    [DockerFact]
    public async Task Two_creates_for_the_same_plan_asset_and_date_leave_one_row()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);
        await using var firstDb = factory.Create(tenantProvider);
        await using var secondDb = factory.Create(tenantProvider);
        var request = new CreateWorkOrderRequest
        {
            AssetId = seed.AssetId,
            MaintenancePlanId = seed.PlanId,
            ScheduledDate = new DateOnly(2026, 10, 17),
            Tasks =
            [
                new CreateWorkOrderTaskDto
                {
                    Title = "Check",
                    InputType = TaskInputType.Text,
                    Order = 1,
                    IsMandatory = false,
                },
            ],
        };

        var first = CreateService(firstDb, tenantProvider).CreateAsync(request, CancellationToken.None);
        var second = CreateService(secondDb, tenantProvider).CreateAsync(request, CancellationToken.None);
        var completed = await Task.WhenAll(
            ObserveAsync(first),
            ObserveAsync(second));

        Assert.Equal(1, completed.Count(item => item is null));
        Assert.Contains(completed, item => item is DuplicateWorkOrderException);
        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(
            1,
            await verify.WorkOrders.CountAsync(workOrder => workOrder.MaintenancePlanId == seed.PlanId));
    }

    private static async Task<bool> WaitForPostgresLockWaitAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        int backendPid)
    {
        await using var observer = factory.Create(tenantProvider);
        await observer.Database.OpenConnectionAsync();
        var connection = observer.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT wait_event_type = 'Lock'
            FROM pg_stat_activity
            WHERE pid = @pid
              AND query ILIKE '%FOR UPDATE%'
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "pid";
        parameter.Value = backendPid;
        command.Parameters.Add(parameter);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var result = await command.ExecuteScalarAsync();
            if (result is true)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory!;
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<Seed> SeedAsync(PostgresAppDbFactory factory, FakeTenantProvider tenantProvider)
    {
        await using var db = factory.Create(tenantProvider);
        var tenant = new Tenant(
            "Lock Tenant",
            Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
            subdomain: $"os-{Guid.NewGuid():N}"[..16]);
        tenantProvider.TenantId = tenant.Id;
        var unit = new Unit(tenant.Id, "Main");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Assets" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = """{"fields":[]}""",
        };
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, family.Id));
        var plan = new MaintenancePlan
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            Name = "Plan",
            IntervalDays = 30,
            FirstDueDate = new DateOnly(2026, 10, 1),
            AssetCategoryId = category.Id,
            IsActive = true,
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Asset",
            Tag = $"Q-{Guid.NewGuid():N}"[..12],
            Status = AssetStatus.Active,
        };
        db.MaintenancePlans.Add(plan);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var workOrder = new WorkOrder
        {
            TenantId = tenant.Id,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = new DateOnly(2026, 10, 16),
        };
        var task = new WorkOrderTask
        {
            TenantId = tenant.Id,
            WorkOrderId = workOrder.Id,
            Title = "Reading",
            InputType = TaskInputType.Text,
            IsMandatory = false,
            Order = 1,
        };
        task.WorkOrder = workOrder;
        workOrder.AddTask(task);
        db.WorkOrders.Add(workOrder);
        await db.SaveChangesAsync();

        return new Seed(tenant.Id, plan.Id, asset.Id, workOrder.Id, task.Id);
    }

    private static WorkOrderService CreateService(AppDbContext db, FakeTenantProvider tenantProvider)
    {
        var http = new FakeHttpContextAccessor();
        var identity = new ClaimsIdentity(
            [new Claim("email", "ops@tenant.test"), new Claim("sub", "admin-sub")],
            authenticationType: "Test");
        http.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var resolver = TestPermissionResolvers.Create(db, tenantProvider);
        var users = new UserDirectoryService(
            db,
            tenantProvider,
            new FakePlatformAdminChecker("ops@tenant.test"),
            resolver,
            new RbacGrantGuard(db, resolver, NullLogger<RbacGrantGuard>.Instance),
            new FakeTrialGuard(),
            new NotificationQueue(),
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(),
            NullLogger<UserDirectoryService>.Instance);
        return new WorkOrderService(
            db,
            tenantProvider,
            http,
            users,
            resolver,
            new AssetRegistry(
                db,
                tenantProvider,
                new AssetService(db, tenantProvider, new FakeTrialGuard()),
                new AssetFamilyService(db),
                new AssetCategoryService(db, tenantProvider)));
    }

    private sealed record Seed(Guid TenantId, Guid PlanId, Guid AssetId, Guid WorkOrderId, Guid TaskId);
}
