using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Create, edit, read and the usage state machine of manual allocations, the version token, the
/// cancel rule, and the overdue rule end to end through <see cref="AssetAllocationService"/>.
/// </summary>
public sealed class AssetAllocationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Base = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    // Monday 09:00 in Sao Paulo, inside weekday working hours.
    private static readonly DateTimeOffset Monday = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Creator = Guid.NewGuid();
    private static readonly Guid Colleague = Guid.NewGuid();

    [Fact]
    public async Task Create_stores_the_window_with_buffers_the_snapshot_and_the_creator()
    {
        await using var rig = await Rig.CreateAsync(prepMinutes: 10, cleanupMinutes: 15);

        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(2), title: "  Print TPU case  "), default);

        Assert.Equal("Print TPU case", created.Title);
        Assert.Equal("Scheduled", created.Status);
        Assert.Equal("Use", created.Kind);
        Assert.Equal("Manual", created.Source);
        Assert.Equal(Base, created.PlannedWorkStart);
        Assert.Equal(Base.AddMinutes(-10), created.OccupiedStart);
        Assert.Equal(Base.AddHours(2).AddMinutes(15), created.OccupiedEnd);
        Assert.Equal("America/Sao_Paulo", created.TimeZoneId);
        Assert.Equal(Creator, created.CreatedByUserId);
        Assert.Equal("Printer A", created.AssetName);
        Assert.True(created.IsBlocking);
        Assert.False(created.IsExpired);
        Assert.False(string.IsNullOrEmpty(created.Version));

        var row = await rig.Db.AssetAllocations.SingleAsync();
        Assert.Equal(rig.Seed.TenantId, row.TenantId);
        Assert.Equal(rig.Seed.UnitId, row.UnitId);
        Assert.Equal(AssetAllocationSource.Manual, row.Source);
    }

    [Fact]
    public async Task Create_normalizes_a_non_utc_offset_to_utc()
    {
        await using var rig = await Rig.CreateAsync();

        var start = new DateTimeOffset(2026, 11, 21, 7, 0, 0, TimeSpan.FromHours(-3));
        var created = await rig.Service.CreateAsync(Request(rig, start, start.AddHours(1)), default);

        Assert.Equal(TimeSpan.Zero, created.PlannedWorkStart.Offset);
        Assert.Equal(Base, created.PlannedWorkStart);
    }

    [Fact]
    public async Task Overlapping_create_is_refused_with_conflicts_and_stores_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1), title: "First"), default);

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, Base.AddMinutes(30), Base.AddHours(2), title: "Second"), default));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(PlanningException.ConflictCode, ex.Code);
        var conflict = Assert.Single(ex.Conflicts!);
        Assert.Equal("First", conflict.Label);
        Assert.Equal(1, await rig.Db.AssetAllocations.CountAsync());
    }

    [Fact]
    public async Task Back_to_back_creates_are_both_accepted()
    {
        await using var rig = await Rig.CreateAsync();

        await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        await rig.Service.CreateAsync(Request(rig, Base.AddHours(1), Base.AddHours(2)), default);

        Assert.Equal(2, await rig.Db.AssetAllocations.CountAsync());
    }

    [Fact]
    public async Task Hours_violation_rejects_the_create_with_422_and_the_reason()
    {
        await using var rig = await Rig.CreateAsync(AssetHoursPolicy.StartWithinHours, weekdayHours: true);

        // Saturday 02:00Z is Friday 23:00 in Sao Paulo: outside 08:00-18:00.
        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, Base.AddHours(-8), Base.AddHours(-6)), default));

        Assert.Equal(422, ex.StatusCode);
        Assert.Equal(PlanningException.OutsideWorkingHoursCode, ex.Code);
        Assert.Equal(AssetWorkingHoursRules.StartOutsideHours, ex.HoursViolation);
        Assert.Empty(await rig.Db.AssetAllocations.ToListAsync());
    }

    [Fact]
    public async Task Asset_closed_all_week_cannot_receive_an_allocation()
    {
        await using var rig = await Rig.CreateAsync(AssetHoursPolicy.WithinHours, weekdayHours: false);

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default));

        Assert.Equal(PlanningException.AssetNotPlannableCode, ex.Code);
    }

    [Theory]
    [InlineData("", "Use")]
    [InlineData("   ", "Use")]
    [InlineData("Job", "Nonsense")]
    public async Task Invalid_title_or_kind_is_rejected_with_400(string title, string kind)
    {
        await using var rig = await Rig.CreateAsync();

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1), title: title, kind: kind), default));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(PlanningException.InvalidAllocationCode, ex.Code);
    }

    [Fact]
    public async Task Too_long_title_and_notes_are_rejected()
    {
        await using var rig = await Rig.CreateAsync();

        await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1), title: new string('x', 201)), default));
        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.CreateAsync(
            Request(rig, Base, Base.AddHours(1)) with { Notes = new string('n', 1001) }, default));
        Assert.Equal(PlanningException.InvalidAllocationCode, ex.Code);
    }

    [Fact]
    public async Task Preview_reports_feasibility_conflicts_and_hours_violation_without_writing()
    {
        await using var rig = await Rig.CreateAsync(AssetHoursPolicy.StartWithinHours, weekdayHours: true);
        await rig.Service.CreateAsync(Request(rig, Monday, Monday.AddHours(1), title: "Held"), default);

        var free = await rig.Service.PreviewAsync(Request(rig, Monday.AddHours(2), Monday.AddHours(3)), default);
        Assert.True(free.Feasible);
        Assert.Empty(free.Conflicts);
        Assert.Null(free.HoursViolation);

        var busy = await rig.Service.PreviewAsync(Request(rig, Monday.AddMinutes(30), Monday.AddHours(2)), default);
        Assert.False(busy.Feasible);
        Assert.Equal("Held", Assert.Single(busy.Conflicts).Label);

        var closed = await rig.Service.PreviewAsync(Request(rig, Monday.AddHours(-8), Monday.AddHours(-6)), default);
        Assert.False(closed.Feasible);
        Assert.Equal(AssetWorkingHoursRules.StartOutsideHours, closed.HoursViolation);

        Assert.Equal(1, await rig.Db.AssetAllocations.CountAsync());
    }

    [Fact]
    public async Task Full_lifecycle_start_work_finished_release_stamps_each_step_and_changes_the_version()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);

        var started = await rig.Service.ApplyUsageAsync(created.Id, Usage("start", created.Version), default);
        Assert.Equal("Running", started.Status);
        Assert.Equal(Now, started.ActualStartAt);
        Assert.NotEqual(created.Version, started.Version);

        var finished = await rig.Service.ApplyUsageAsync(created.Id, Usage("work-finished", started.Version), default);
        Assert.Equal("Cleaning", finished.Status);
        Assert.Equal(Now, finished.ActualWorkFinishedAt);

        var released = await rig.Service.ApplyUsageAsync(created.Id, Usage("release", finished.Version), default);
        Assert.Equal("Released", released.Status);
        Assert.Equal(Now, released.ActualReleaseAt);
        Assert.False(released.IsBlocking);

        // Released frees the window for a new allocation.
        await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1), title: "Reuse"), default);
    }

    [Theory]
    [InlineData("work-finished", "Scheduled")]
    [InlineData("release", "Scheduled")]
    [InlineData("start", "Running")]
    [InlineData("release", "Running")]
    [InlineData("cancel", "Running")]
    [InlineData("start", "Cleaning")]
    [InlineData("work-finished", "Cleaning")]
    [InlineData("cancel", "Cleaning")]
    [InlineData("start", "Released")]
    [InlineData("work-finished", "Released")]
    [InlineData("release", "Released")]
    [InlineData("cancel", "Released")]
    [InlineData("start", "Cancelled")]
    [InlineData("cancel", "Cancelled")]
    public async Task State_machine_rejects_every_out_of_order_action_with_409(string action, string fromStatus)
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        var current = await rig.MoveToAsync(created, fromStatus);

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.ApplyUsageAsync(created.Id, Usage(action, current.Version), default));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(PlanningException.InvalidTransitionCode, ex.Code);
        Assert.Equal(fromStatus, (await rig.Service.GetAsync(created.Id, default)).Status);
    }

    [Fact]
    public async Task Cancel_of_a_scheduled_allocation_frees_the_window_and_keeps_the_reason()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);

        var cancelled = await rig.Service.ApplyUsageAsync(
            created.Id, Usage("cancel", created.Version, reason: "  customer gave up "), default);

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("customer gave up", cancelled.CancelReason);
        Assert.False(cancelled.IsBlocking);
        await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1), title: "Reuse"), default);
    }

    [Fact]
    public async Task Stale_version_is_a_409_version_conflict_and_changes_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        await rig.Service.ApplyUsageAsync(created.Id, Usage("start", created.Version), default);

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.ApplyUsageAsync(created.Id, Usage("cancel", created.Version), default));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(PlanningException.VersionConflictCode, ex.Code);
        Assert.Equal("Running", (await rig.Service.GetAsync(created.Id, default)).Status);
    }

    [Fact]
    public async Task Stale_version_on_edit_is_a_409_version_conflict()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        await rig.Service.UpdateAsync(created.Id, new UpdateAssetAllocationRequest
        {
            ExpectedVersion = created.Version,
            Title = "Edited once",
        }, default);

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.UpdateAsync(
            created.Id,
            new UpdateAssetAllocationRequest { ExpectedVersion = created.Version, Title = "Edited twice" },
            default));

        Assert.Equal(PlanningException.VersionConflictCode, ex.Code);
        Assert.Equal("Edited once", (await rig.Service.GetAsync(created.Id, default)).Title);
    }

    [Fact]
    public async Task Missing_version_and_unknown_action_are_400()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);

        var noVersion = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.ApplyUsageAsync(created.Id, Usage("start", null), default));
        Assert.Equal(PlanningException.InvalidAllocationCode, noVersion.Code);

        var badAction = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.ApplyUsageAsync(created.Id, Usage("explode", created.Version), default));
        Assert.Equal(400, badAction.StatusCode);
    }

    [Fact]
    public async Task Cancel_by_the_creator_is_allowed_without_the_manage_right()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);

        var cancelled = await rig.Service.ApplyUsageAsync(created.Id, Usage("cancel", created.Version), default);

        Assert.Equal("Cancelled", cancelled.Status);
    }

    [Fact]
    public async Task Cancel_of_someone_elses_allocation_is_403_unless_the_caller_manages_allocations()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);

        rig.Caller.UserId = Colleague;
        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.ApplyUsageAsync(created.Id, Usage("cancel", created.Version), default));
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal(PlanningException.CancelForbiddenCode, ex.Code);
        Assert.Equal("Scheduled", (await rig.Service.GetAsync(created.Id, default)).Status);

        // Other actions stay open to the colleague: a technician starts and releases anyone's job.
        var started = await rig.Service.ApplyUsageAsync(created.Id, Usage("start", created.Version), default);
        Assert.Equal("Running", started.Status);

        rig.Caller.CanManage = true;
        var second = await rig.Service.CreateAsync(
            Request(rig, Base.AddHours(3), Base.AddHours(4), assetId: rig.Seed.SecondAssetId), default);
        rig.Caller.UserId = Guid.NewGuid();
        var byManager = await rig.Service.ApplyUsageAsync(second.Id, Usage("cancel", second.Version), default);
        Assert.Equal("Cancelled", byManager.Status);
    }

    [Fact]
    public async Task Caller_without_a_user_row_cannot_cancel_an_allocation_someone_created()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        rig.Caller.UserId = null;

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.ApplyUsageAsync(created.Id, Usage("cancel", created.Version), default));

        Assert.Equal(PlanningException.CancelForbiddenCode, ex.Code);
    }

    [Fact]
    public async Task Scheduled_allocation_that_was_never_started_expires_after_its_end_and_cannot_be_started()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);

        rig.Clock.SetUtcNow(Base.AddHours(1));
        var expired = await rig.Service.GetAsync(created.Id, default);
        Assert.True(expired.IsExpired);
        Assert.False(expired.IsBlocking);

        // Its window is free for someone else...
        await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1), title: "Took the slot"), default);

        // ... so reviving it with a start must not make two allocations hold one asset.
        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.ApplyUsageAsync(created.Id, Usage("start", expired.Version), default));
        Assert.Equal(PlanningException.InvalidTransitionCode, ex.Code);

        // An expired allocation can still be tidied away by cancelling it.
        var cancelled = await rig.Service.ApplyUsageAsync(created.Id, Usage("cancel", expired.Version), default);
        Assert.Equal("Cancelled", cancelled.Status);
    }

    [Fact]
    public async Task Started_allocation_blocks_past_its_end_and_is_reported_overdue_until_released()
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        await rig.Service.ApplyUsageAsync(created.Id, Usage("start", created.Version), default);

        rig.Clock.SetUtcNow(Base.AddDays(2));
        var overdue = await rig.Service.GetAsync(created.Id, default);
        Assert.True(overdue.IsOverdue);
        Assert.True(overdue.IsBlocking);
        Assert.False(overdue.IsExpired);

        var ex = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.CreateAsync(Request(rig, Base.AddDays(3), Base.AddDays(3).AddHours(1)), default));
        Assert.Equal(PlanningException.ConflictCode, ex.Code);
    }

    [Fact]
    public async Task Edit_changes_title_notes_and_window_and_rechecks_conflicts_ignoring_itself()
    {
        await using var rig = await Rig.CreateAsync();
        var first = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        await rig.Service.CreateAsync(Request(rig, Base.AddHours(3), Base.AddHours(4), title: "Other"), default);

        // Moves within its own old window: not a conflict with itself.
        var moved = await rig.Service.UpdateAsync(first.Id, new UpdateAssetAllocationRequest
        {
            ExpectedVersion = first.Version,
            Title = "Renamed",
            Notes = "bring spare nozzle",
            WorkStart = Base.AddMinutes(15),
            WorkEnd = Base.AddMinutes(45),
        }, default);
        Assert.Equal("Renamed", moved.Title);
        Assert.Equal("bring spare nozzle", moved.Notes);
        Assert.Equal(Base.AddMinutes(15), moved.PlannedWorkStart);
        Assert.NotEqual(first.Version, moved.Version);

        // Moving onto the other allocation is refused.
        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.UpdateAsync(first.Id, new UpdateAssetAllocationRequest
        {
            ExpectedVersion = moved.Version,
            WorkStart = Base.AddHours(3),
            WorkEnd = Base.AddHours(3).AddMinutes(30),
        }, default));
        Assert.Equal(PlanningException.ConflictCode, ex.Code);
        Assert.Equal("Other", Assert.Single(ex.Conflicts!).Label);

        // An empty notes string clears them.
        var cleared = await rig.Service.UpdateAsync(
            first.Id, new UpdateAssetAllocationRequest { ExpectedVersion = moved.Version, Notes = "" }, default);
        Assert.Null(cleared.Notes);
    }

    [Fact]
    public async Task Edit_keeps_the_existing_buffers_unless_the_caller_changes_them()
    {
        await using var rig = await Rig.CreateAsync(prepMinutes: 10, cleanupMinutes: 20);
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);

        var moved = await rig.Service.UpdateAsync(created.Id, new UpdateAssetAllocationRequest
        {
            ExpectedVersion = created.Version,
            WorkStart = Base.AddHours(2),
            WorkEnd = Base.AddHours(3),
        }, default);

        Assert.Equal(Base.AddHours(2).AddMinutes(-10), moved.OccupiedStart);
        Assert.Equal(Base.AddHours(3).AddMinutes(20), moved.OccupiedEnd);
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Cancelled")]
    public async Task Edit_is_only_allowed_while_scheduled(string status)
    {
        await using var rig = await Rig.CreateAsync();
        var created = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        var current = await rig.MoveToAsync(created, status);

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.UpdateAsync(
            created.Id, new UpdateAssetAllocationRequest { ExpectedVersion = current.Version, Title = "x" }, default));

        Assert.Equal(PlanningException.InvalidTransitionCode, ex.Code);
    }

    [Fact]
    public async Task Allocation_of_an_order_workflow_cannot_be_edited_or_driven_here()
    {
        await using var rig = await Rig.CreateAsync();
        var row = new AssetAllocation
        {
            TenantId = rig.Seed.TenantId,
            AssetId = rig.Seed.AssetId,
            UnitId = rig.Seed.UnitId,
            Title = "Pedido 12",
            PlannedWorkStart = Base,
            PlannedWorkEnd = Base.AddHours(1),
            OccupiedStart = Base,
            OccupiedEnd = Base.AddHours(1),
            TimeZoneId = "America/Sao_Paulo",
            Source = AssetAllocationSource.CatalogOrder,
        };
        rig.Db.AssetAllocations.Add(row);
        await rig.Db.SaveChangesAsync();
        var version = Convert.ToBase64String(row.RowVersion);

        var usage = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Service.ApplyUsageAsync(row.Id, Usage("start", version), default));
        Assert.Equal(409, usage.StatusCode);
        Assert.Equal(PlanningException.OrderWorkflowRequiredCode, usage.Code);

        var edit = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.UpdateAsync(
            row.Id, new UpdateAssetAllocationRequest { ExpectedVersion = version, Title = "x" }, default));
        Assert.Equal(PlanningException.OrderWorkflowRequiredCode, edit.Code);
    }

    [Fact]
    public async Task Unknown_allocation_and_other_tenant_allocation_are_404()
    {
        await using var rig = await Rig.CreateAsync();
        var foreign = new AssetAllocation
        {
            TenantId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            Title = "Foreign",
            PlannedWorkStart = Base,
            PlannedWorkEnd = Base.AddHours(1),
            OccupiedStart = Base,
            OccupiedEnd = Base.AddHours(1),
            TimeZoneId = "America/Sao_Paulo",
        };
        rig.Db.AssetAllocations.Add(foreign);
        await rig.Db.SaveChangesAsync();

        foreach (var id in new[] { Guid.NewGuid(), foreign.Id })
        {
            var get = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.GetAsync(id, default));
            Assert.Equal(PlanningException.AllocationNotFoundCode, get.Code);
            var usage = await Assert.ThrowsAsync<PlanningException>(
                () => rig.Service.ApplyUsageAsync(id, Usage("start", "AAAA"), default));
            Assert.Equal(404, usage.StatusCode);
            var update = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.UpdateAsync(
                id, new UpdateAssetAllocationRequest { ExpectedVersion = "AAAA", Title = "x" }, default));
            Assert.Equal(404, update.StatusCode);
        }

        Assert.Empty(await rig.Service.ListAsync(null, null, default));
    }

    [Fact]
    public async Task List_filters_by_asset_and_status_and_rejects_an_unknown_status()
    {
        await using var rig = await Rig.CreateAsync();
        var one = await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1)), default);
        await rig.Service.CreateAsync(Request(rig, Base, Base.AddHours(1), assetId: rig.Seed.SecondAssetId), default);
        await rig.Service.ApplyUsageAsync(one.Id, Usage("cancel", one.Version), default);

        Assert.Equal(2, (await rig.Service.ListAsync(null, null, default)).Count);
        Assert.Single(await rig.Service.ListAsync(rig.Seed.SecondAssetId, null, default));
        Assert.Single(await rig.Service.ListAsync(null, "cancelled", default));
        Assert.Single(await rig.Service.ListAsync(null, "Scheduled", default));

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.Service.ListAsync(null, "bogus", default));
        Assert.Equal(400, ex.StatusCode);
    }

    private static CreateAssetAllocationRequest Request(
        Rig rig,
        DateTimeOffset start,
        DateTimeOffset end,
        string title = "Print job",
        string kind = "Use",
        Guid? assetId = null) =>
        new()
        {
            AssetId = assetId ?? rig.Seed.AssetId,
            Kind = kind,
            Title = title,
            WorkStart = start,
            WorkEnd = end,
        };

    private static AssetAllocationUsageRequest Usage(string action, string? version, string? reason = null) =>
        new() { Action = action, ExpectedVersion = version, Reason = reason };

    internal sealed class Rig : IAsyncDisposable
    {
        private Rig(AppDbContext db, PlanningTenantSeed seed, TestTimeProvider clock, FakePlanningCaller caller)
        {
            Db = db;
            Seed = seed;
            Clock = clock;
            Caller = caller;
            var accessor = new FixedModuleAccessor(PlatformModules.ResourcePlanning);
            var tenantProvider = new FakeTenantProvider { TenantId = seed.TenantId };
            var occupancy = new AssetOccupancyService(db, accessor, clock);
            Service = new AssetAllocationService(db, tenantProvider, occupancy, caller, clock);
            Agenda = new PlanningAgendaService(db, tenantProvider, accessor, clock);
        }

        public AppDbContext Db { get; }

        public PlanningTenantSeed Seed { get; }

        public TestTimeProvider Clock { get; }

        public FakePlanningCaller Caller { get; }

        public AssetAllocationService Service { get; }

        public PlanningAgendaService Agenda { get; }

        public static async Task<Rig> CreateAsync(
            AssetHoursPolicy policy = AssetHoursPolicy.Anytime,
            bool weekdayHours = false,
            int prepMinutes = 0,
            int cleanupMinutes = 0)
        {
            var provider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(provider);
            var seed = PlanningTestData.AddTenant(db, policy: policy, prepMinutes: prepMinutes, cleanupMinutes: cleanupMinutes);
            provider.TenantId = seed.TenantId;
            if (weekdayHours)
            {
                foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
                {
                    db.AssetWorkingHours.Add(PlanningTestData.Hours(seed.TenantId, seed.AssetId, day, 8, 18));
                }
            }

            await db.SaveChangesAsync();
            return new Rig(db, seed, new TestTimeProvider(Now), new FakePlanningCaller(Creator, canManage: false));
        }

        /// <summary>Walks a freshly created (Scheduled) allocation to the status and returns its response.</summary>
        public async Task<AssetAllocationResponse> MoveToAsync(AssetAllocationResponse created, string status)
        {
            var current = created;
            string[] path = status switch
            {
                "Scheduled" => [],
                "Running" => ["start"],
                "Cleaning" => ["start", "work-finished"],
                "Released" => ["start", "work-finished", "release"],
                "Cancelled" => ["cancel"],
                _ => throw new ArgumentOutOfRangeException(nameof(status)),
            };
            foreach (var action in path)
            {
                current = await Service.ApplyUsageAsync(
                    created.Id,
                    new AssetAllocationUsageRequest { Action = action, ExpectedVersion = current.Version },
                    default);
            }

            return current;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
