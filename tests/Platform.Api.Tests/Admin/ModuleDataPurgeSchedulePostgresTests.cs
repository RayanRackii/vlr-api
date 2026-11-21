using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Services.Tenancy;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Real-PostgreSQL coverage of scheduling and cancelling a module data purge (spec slice API-3): the
/// pending row and its 30 day date, the one-pending-per-module rule (service guard, partial unique
/// index and a concurrent double schedule), the typed confirmation, blockers, cancel with audit fields.
/// The tables come from the real migrations (the fixture runs <c>MigrateAsync</c>), so this also
/// exercises the hand-written migration of <c>core.tenant_module_data_purges</c>.
/// </summary>
public sealed class ModuleDataPurgeSchedulePostgresTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public ModuleDataPurgeSchedulePostgresTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Scheduling_creates_a_pending_row_thirty_days_ahead()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);

        var before = DateTimeOffset.UtcNow;
        var scheduled = await ScheduleAsync(factory, target.TenantId, "catalog", "catalog", "auth-sub-1");
        var after = DateTimeOffset.UtcNow;

        Assert.Equal("catalog", scheduled.Module);
        Assert.InRange(scheduled.RequestedAt, before, after);
        Assert.Equal(scheduled.RequestedAt + TimeSpan.FromDays(30), scheduled.ScheduledFor);

        await using var db = factory.Create(new FakeTenantProvider());
        var row = await db.TenantModuleDataPurges.AsNoTracking().SingleAsync(p => p.Id == scheduled.Id);
        Assert.Equal(target.TenantId, row.TenantId);
        Assert.Equal("catalog", row.Module);
        Assert.Equal("auth-sub-1", row.RequestedByUserId);
        AssertSameInstant(scheduled.ScheduledFor, row.ScheduledFor);
        Assert.Null(row.CancelledAt);
        Assert.Null(row.ExecutedAt);
        Assert.Null(row.ResultJson);

        // Nothing is deleted by scheduling, and the preview now shows the pending purge.
        var preview = await PreviewAsync(factory, target.TenantId, "catalog");
        Assert.Equal(scheduled.Id, preview.Pending!.Id);
        AssertSameInstant(scheduled.ScheduledFor, preview.Pending.ScheduledFor);
        Assert.Equal(1, Rows(preview, "catalog.catalog_orders"));
    }

    [DockerFact]
    public async Task The_confirmation_must_be_the_module_key()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);

        var wrong = await Assert.ThrowsAsync<ModuleDataPurgeRequestException>(
            () => ScheduleAsync(factory, target.TenantId, "catalog", "rentals", null));
        Assert.Equal(ModuleDataPurgeService.ConfirmMismatchCode, wrong.Code);
        var missing = await Assert.ThrowsAsync<ModuleDataPurgeRequestException>(
            () => ScheduleAsync(factory, target.TenantId, "catalog", null, null));
        Assert.Equal(ModuleDataPurgeService.ConfirmMismatchCode, missing.Code);
        var notPurgeable = await Assert.ThrowsAsync<ModuleDataPurgeRequestException>(
            () => ScheduleAsync(factory, target.TenantId, "inventory", "inventory", null));
        Assert.Equal(ModuleDataScope.NotPurgeableCode, notPurgeable.Code);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => ScheduleAsync(factory, Guid.NewGuid(), "catalog", "catalog", null));

        Assert.Equal(0, await PurgeRowsAsync(factory, target.TenantId));
    }

    [DockerFact]
    public async Task A_second_schedule_for_the_same_module_is_a_conflict_and_other_modules_are_independent()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        await ScheduleAsync(factory, target.TenantId, "catalog", "catalog", null);

        var conflict = await Assert.ThrowsAsync<ModuleDataPurgeConflictException>(
            () => ScheduleAsync(factory, target.TenantId, "catalog", "catalog", null));
        Assert.Equal(ModuleDataPurgeService.AlreadyPendingCode, conflict.Code);
        Assert.Equal(1, await PurgeRowsAsync(factory, target.TenantId));

        // os is a different (module, tenant) pair, so it can be scheduled next to it.
        await ScheduleAsync(factory, target.TenantId, "os", "os", null);
        Assert.Equal(2, await PurgeRowsAsync(factory, target.TenantId));
    }

    [DockerFact]
    public async Task The_database_itself_allows_one_pending_purge_per_tenant_and_module()
    {
        var factory = RequireFactory();
        var tenantId = await ModuleDataPurgePreviewPostgresTests.SeedEmptyTenantAsync(factory);
        await InsertPurgeAsync(factory, tenantId, "catalog", cancelled: false);

        // A second pending row is refused by the partial unique index...
        await Assert.ThrowsAsync<DbUpdateException>(() => InsertPurgeAsync(factory, tenantId, "catalog", cancelled: false));
        // ...but cancelled history rows and other modules are fine.
        await InsertPurgeAsync(factory, tenantId, "catalog", cancelled: true);
        await InsertPurgeAsync(factory, tenantId, "catalog", cancelled: true);
        await InsertPurgeAsync(factory, tenantId, "os", cancelled: false);
        Assert.Equal(4, await PurgeRowsAsync(factory, tenantId));
    }

    [DockerFact]
    public async Task Two_concurrent_schedules_create_exactly_one_purge()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);

        var results = await Task.WhenAll(
            Capture(() => ScheduleAsync(factory, target.TenantId, "catalog", "catalog", "a")),
            Capture(() => ScheduleAsync(factory, target.TenantId, "catalog", "catalog", "b")));

        Assert.Equal(1, results.Count(error => error is null));
        Assert.Single(results, error => error is ModuleDataPurgeConflictException);
        Assert.Equal(1, await PurgeRowsAsync(factory, target.TenantId));
    }

    [DockerFact]
    public async Task An_active_module_cannot_be_scheduled()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: true);

        var blocked = await Assert.ThrowsAsync<ModuleDataPurgeBlockedException>(
            () => ScheduleAsync(factory, target.TenantId, "catalog", "catalog", null));

        Assert.True(blocked.Preview.ModuleActive);
        Assert.Contains(blocked.Preview.Blockers, b => b.Code == ModuleDataPurgeService.ModuleActiveCode);
        Assert.Equal(0, await PurgeRowsAsync(factory, target.TenantId));
    }

    [DockerFact]
    public async Task Blockers_refuse_the_schedule_and_come_back_with_the_preview()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);

        // The catalog rows point at rental assets and reservation items, so rentals must wait.
        var blocked = await Assert.ThrowsAsync<ModuleDataPurgeBlockedException>(
            () => ScheduleAsync(factory, target.TenantId, "rentals", "rentals", null));

        Assert.False(blocked.Preview.ModuleActive);
        Assert.Contains(
            blocked.Preview.Blockers,
            b => b.Code == ModuleDataPurgeService.ForeignKeyRestrictCode
                && b.Table == "catalog.order_item_resource_allocations");
        Assert.Equal(2, Rows(blocked.Preview, "rentals.reservations"));
        Assert.Equal(0, await PurgeRowsAsync(factory, target.TenantId));
    }

    [DockerFact]
    public async Task Cancelling_records_who_when_and_why_and_frees_the_slot()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var scheduled = await ScheduleAsync(factory, target.TenantId, "catalog", "catalog", "auth-sub-1");

        var before = DateTimeOffset.UtcNow;
        await CancelAsync(factory, target.TenantId, "catalog", "  kept by mistake  ", "auth-sub-2");
        var after = DateTimeOffset.UtcNow;

        await using (var db = factory.Create(new FakeTenantProvider()))
        {
            var row = await db.TenantModuleDataPurges.AsNoTracking().SingleAsync(p => p.Id == scheduled.Id);
            Assert.InRange(row.CancelledAt!.Value, before.AddMilliseconds(-1), after.AddMilliseconds(1));
            Assert.Equal("auth-sub-2", row.CancelledByUserId);
            Assert.Equal("kept by mistake", row.CancelReason);
            Assert.Null(row.ExecutedAt);
            Assert.NotNull(row.UpdatedAt);
        }

        Assert.Null((await PreviewAsync(factory, target.TenantId, "catalog")).Pending);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CancelAsync(factory, target.TenantId, "catalog", null, null));

        // The slot is free again, and a cancel without a reason is fine.
        var again = await ScheduleAsync(factory, target.TenantId, "catalog", "catalog", null);
        await CancelAsync(factory, target.TenantId, "catalog", null, null);
        await using var check = factory.Create(new FakeTenantProvider());
        var second = await check.TenantModuleDataPurges.AsNoTracking().SingleAsync(p => p.Id == again.Id);
        Assert.NotNull(second.CancelledAt);
        Assert.Null(second.CancelReason);
        Assert.Null(second.CancelledByUserId);
    }

    [DockerFact]
    public async Task A_reason_that_is_too_long_or_a_bad_module_is_refused_on_cancel()
    {
        var factory = RequireFactory();
        var target = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        await ScheduleAsync(factory, target.TenantId, "catalog", "catalog", null);

        var tooLong = await Assert.ThrowsAsync<ModuleDataPurgeRequestException>(
            () => CancelAsync(factory, target.TenantId, "catalog", new string('x', 501), null));
        Assert.Equal(ModuleDataPurgeService.ReasonTooLongCode, tooLong.Code);
        await Assert.ThrowsAsync<ModuleDataPurgeRequestException>(
            () => CancelAsync(factory, target.TenantId, "bogus", null, null));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CancelAsync(factory, Guid.NewGuid(), "catalog", null, null));

        Assert.NotNull((await PreviewAsync(factory, target.TenantId, "catalog")).Pending);
    }

    [DockerFact]
    public async Task Another_tenants_purge_is_never_cancelled_or_reported_as_pending()
    {
        var factory = RequireFactory();
        var first = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        var second = await ModuleDataPurgePreviewPostgresTests.SeedAsync(factory, modulesActive: false);
        await ScheduleAsync(factory, first.TenantId, "catalog", "catalog", null);

        Assert.Null((await PreviewAsync(factory, second.TenantId, "catalog")).Pending);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CancelAsync(factory, second.TenantId, "catalog", null, null));
        Assert.NotNull((await PreviewAsync(factory, first.TenantId, "catalog")).Pending);
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static void AssertSameInstant(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.True(
            Math.Abs((expected - actual).TotalMilliseconds) < 1,
            $"Expected {expected:O} but got {actual:O} (timestamps are stored with microsecond precision).");

    private static long Rows(ModuleDataPurgePreviewDto preview, string table) =>
        preview.Tables.SingleOrDefault(t => t.Table == table)?.Rows ?? 0;

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<ModuleDataPurgeScheduledDto> ScheduleAsync(
        PostgresAppDbFactory factory,
        Guid tenantId,
        string module,
        string? confirm,
        string? actor)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var service = new ModuleDataPurgeService(db, NullLogger<ModuleDataPurgeService>.Instance);
        return await service.ScheduleAsync(tenantId, module, confirm, actor, CancellationToken.None);
    }

    private static async Task CancelAsync(
        PostgresAppDbFactory factory,
        Guid tenantId,
        string module,
        string? reason,
        string? actor)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var service = new ModuleDataPurgeService(db, NullLogger<ModuleDataPurgeService>.Instance);
        await service.CancelAsync(tenantId, module, reason, actor, CancellationToken.None);
    }

    private static async Task<ModuleDataPurgePreviewDto> PreviewAsync(
        PostgresAppDbFactory factory,
        Guid tenantId,
        string module)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var service = new ModuleDataPurgeService(db, NullLogger<ModuleDataPurgeService>.Instance);
        return await service.PreviewAsync(tenantId, module, CancellationToken.None);
    }

    private static async Task<int> PurgeRowsAsync(PostgresAppDbFactory factory, Guid tenantId)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        return await db.TenantModuleDataPurges.AsNoTracking().CountAsync(p => p.TenantId == tenantId);
    }

    private static async Task InsertPurgeAsync(PostgresAppDbFactory factory, Guid tenantId, string module, bool cancelled)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var now = DateTimeOffset.UtcNow;
        var purge = new TenantModuleDataPurge
        {
            TenantId = tenantId,
            Module = module,
            RequestedAt = now,
            ScheduledFor = now.AddDays(30),
        };
        if (cancelled)
            purge.Cancel(now, null, "test");
        db.TenantModuleDataPurges.Add(purge);
        await db.SaveChangesAsync();
    }
}
