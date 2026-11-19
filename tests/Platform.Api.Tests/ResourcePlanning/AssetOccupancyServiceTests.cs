using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// The planning-only occupancy backend: the overlap matrix (half-open edges, buffers, capacity),
/// the blocking rule per status, the overdue and expiry rule, the hours policy, the 30-day cap and
/// the eligibility rules (L7). In-memory: the row lock is a no-op there and is covered on
/// PostgreSQL by <c>PlanningAllocationConcurrencyTests</c>.
/// </summary>
public sealed class AssetOccupancyServiceTests
{
    // 2026-11-20 is a Friday; 15:00Z is 12:00 in Sao Paulo (UTC-3, no DST).
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Base = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-60, 0, true)]     // ends exactly when the existing one starts
    [InlineData(60, 120, true)]    // starts exactly when the existing one ends
    [InlineData(-1, 1, false)]     // one minute into the existing one
    [InlineData(59, 61, false)]    // one minute before its end
    [InlineData(15, 45, false)]    // inside
    [InlineData(-60, 120, false)]  // contains it
    [InlineData(0, 60, false)]     // identical
    public async Task Half_open_edges_touching_windows_do_not_conflict(int startMinutes, int endMinutes, bool expectedFree)
    {
        await using var rig = await Rig.CreateAsync();
        rig.AddAllocation(Base, Base.AddMinutes(60));
        await rig.SaveAsync();

        var result = await rig.CheckAsync(Base.AddMinutes(startMinutes), Base.AddMinutes(endMinutes));

        Assert.Equal(expectedFree, result.IsFree);
    }

    [Fact]
    public async Task Cleanup_buffer_of_an_existing_allocation_blocks_the_window_after_its_work_end()
    {
        await using var rig = await Rig.CreateAsync();
        rig.AddAllocation(Base, Base.AddMinutes(60), cleanupMinutes: 30);
        await rig.SaveAsync();

        Assert.False((await rig.CheckAsync(Base.AddMinutes(60), Base.AddMinutes(90))).IsFree);
        Assert.True((await rig.CheckAsync(Base.AddMinutes(90), Base.AddMinutes(120))).IsFree);
    }

    [Fact]
    public async Task Prep_buffer_of_the_request_is_part_of_the_window_it_needs()
    {
        await using var rig = await Rig.CreateAsync();
        rig.AddAllocation(Base, Base.AddMinutes(60));
        await rig.SaveAsync();

        // Work starts right when the existing one ends, but 30 minutes of prep reach back into it.
        Assert.False((await rig.CheckAsync(Base.AddMinutes(60), Base.AddMinutes(120), prepMinutes: 30)).IsFree);
        Assert.True((await rig.CheckAsync(Base.AddMinutes(90), Base.AddMinutes(120), prepMinutes: 30)).IsFree);
    }

    [Fact]
    public async Task Request_cleanup_buffer_is_part_of_the_window_it_needs()
    {
        await using var rig = await Rig.CreateAsync();
        rig.AddAllocation(Base.AddMinutes(120), Base.AddMinutes(180));
        await rig.SaveAsync();

        Assert.False((await rig.CheckAsync(Base, Base.AddMinutes(90), cleanupMinutes: 45)).IsFree);
        Assert.True((await rig.CheckAsync(Base, Base.AddMinutes(75), cleanupMinutes: 45)).IsFree);
    }

    [Fact]
    public async Task Default_buffers_of_the_profile_apply_when_the_request_gives_none()
    {
        await using var rig = await Rig.CreateAsync(prepMinutes: 10, cleanupMinutes: 20);
        var asset = await rig.ResolveAsync();

        var window = rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Use, Base, Base.AddMinutes(60), prepMinutes: null, cleanupMinutes: null);

        Assert.Equal(Base.AddMinutes(-10), window.OccupiedStart);
        Assert.Equal(Base.AddMinutes(80), window.OccupiedEnd);
        Assert.Equal(Base, window.WorkStart);
        Assert.Equal(Base.AddMinutes(60), window.WorkEnd);
    }

    [Fact]
    public async Task Capacity_one_rejects_any_overlap()
    {
        await using var rig = await Rig.CreateAsync(capacity: 1);
        rig.AddAllocation(Base, Base.AddMinutes(60));
        await rig.SaveAsync();

        var result = await rig.CheckAsync(Base.AddMinutes(30), Base.AddMinutes(90));

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("allocation", conflict.Source);
        Assert.Equal("Existing job", conflict.Label);
        Assert.Equal(Base, conflict.Start);
        Assert.Equal(Base.AddMinutes(60), conflict.End);
    }

    [Fact]
    public async Task Capacity_two_accepts_one_overlap_and_rejects_the_second()
    {
        await using var rig = await Rig.CreateAsync(capacity: 2);
        rig.AddAllocation(Base, Base.AddMinutes(60));
        await rig.SaveAsync();
        Assert.True((await rig.CheckAsync(Base, Base.AddMinutes(60))).IsFree);

        rig.AddAllocation(Base, Base.AddMinutes(60));
        await rig.SaveAsync();
        var result = await rig.CheckAsync(Base, Base.AddMinutes(60));

        Assert.False(result.IsFree);
        Assert.Equal(2, result.Conflicts.Count);
    }

    [Fact]
    public async Task Capacity_counts_concurrent_quantity_not_the_sum_of_all_overlaps()
    {
        // A [0,60) and B [60,120) never run together, so a request over both needs one unit.
        await using var rig = await Rig.CreateAsync(capacity: 2);
        rig.AddAllocation(Base, Base.AddMinutes(60));
        rig.AddAllocation(Base.AddMinutes(60), Base.AddMinutes(120));
        await rig.SaveAsync();
        Assert.True((await rig.CheckAsync(Base, Base.AddMinutes(120))).IsFree);

        // C overlaps A during [30,60): two units are busy there, so a request touching it is full.
        rig.AddAllocation(Base.AddMinutes(30), Base.AddMinutes(90));
        await rig.SaveAsync();
        Assert.False((await rig.CheckAsync(Base.AddMinutes(40), Base.AddMinutes(50))).IsFree);
        Assert.True((await rig.CheckAsync(Base.AddMinutes(90), Base.AddMinutes(120))).IsFree);
    }

    [Fact]
    public async Task Quantity_of_an_existing_allocation_counts_against_capacity()
    {
        await using var rig = await Rig.CreateAsync(capacity: 3);
        rig.AddAllocation(Base, Base.AddMinutes(60), quantity: 3);
        await rig.SaveAsync();

        Assert.False((await rig.CheckAsync(Base, Base.AddMinutes(60))).IsFree);
    }

    [Fact]
    public async Task Unavailable_block_takes_the_whole_capacity_in_both_directions()
    {
        await using var rig = await Rig.CreateAsync(capacity: 2);
        rig.AddAllocation(Base, Base.AddMinutes(60), kind: AssetAllocationKind.Unavailable);
        await rig.SaveAsync();
        Assert.False((await rig.CheckAsync(Base, Base.AddMinutes(60))).IsFree);

        // The reverse: a block over a job is refused, even with a free unit left.
        await using var other = await Rig.CreateAsync(capacity: 2);
        other.AddAllocation(Base, Base.AddMinutes(60));
        await other.SaveAsync();
        Assert.False((await other.CheckAsync(Base, Base.AddMinutes(60), kind: AssetAllocationKind.Unavailable)).IsFree);
    }

    [Theory]
    [InlineData(AllocationStatus.Released)]
    [InlineData(AllocationStatus.Cancelled)]
    public async Task Released_and_cancelled_allocations_never_block(AllocationStatus status)
    {
        await using var rig = await Rig.CreateAsync();
        rig.AddAllocation(Base, Base.AddMinutes(60), status: status);
        await rig.SaveAsync();

        Assert.True((await rig.CheckAsync(Base, Base.AddMinutes(60))).IsFree);
    }

    [Fact]
    public async Task Other_assets_and_other_tenants_do_not_block()
    {
        await using var rig = await Rig.CreateAsync();
        rig.AddAllocation(Base, Base.AddMinutes(60), assetId: rig.Seed.SecondAssetId);
        rig.Db.AssetAllocations.Add(new AssetAllocation
        {
            TenantId = Guid.NewGuid(),
            AssetId = rig.Seed.AssetId,
            UnitId = rig.Seed.UnitId,
            Title = "Foreign tenant job",
            PlannedWorkStart = Base,
            PlannedWorkEnd = Base.AddMinutes(60),
            OccupiedStart = Base,
            OccupiedEnd = Base.AddMinutes(60),
            TimeZoneId = "America/Sao_Paulo",
        });
        await rig.SaveAsync();

        Assert.True((await rig.CheckAsync(Base, Base.AddMinutes(60))).IsFree);
    }

    [Fact]
    public async Task Excluded_allocation_is_ignored_so_an_edit_can_keep_its_own_window()
    {
        await using var rig = await Rig.CreateAsync();
        var own = rig.AddAllocation(Base, Base.AddMinutes(60));
        await rig.SaveAsync();

        var asset = await rig.ResolveAsync();
        var window = rig.Occupancy.ValidateWindow(asset, AssetAllocationKind.Use, Base, Base.AddMinutes(60), 0, 0);
        var excluded = await rig.Occupancy.CheckAvailabilityAsync(
            rig.Seed.TenantId, asset, AssetAllocationKind.Use, window, own.Id, default);

        Assert.True(excluded.IsFree);
    }

    [Fact]
    public async Task Scheduled_manual_allocation_never_started_stops_blocking_after_its_end()
    {
        await using var rig = await Rig.CreateAsync();
        var past = Now.AddHours(-5);
        rig.AddAllocation(past, past.AddHours(1));
        await rig.SaveAsync();

        // Its window has passed: it no longer blocks the same past window or anything after.
        Assert.True((await rig.CheckAsync(past, past.AddHours(1))).IsFree);
        Assert.True((await rig.CheckAsync(Now.AddHours(1), Now.AddHours(2))).IsFree);

        // The same row is still blocking while its end is in the future.
        rig.Clock.SetUtcNow(past.AddMinutes(30));
        Assert.False((await rig.CheckAsync(past, past.AddHours(1))).IsFree);
        rig.Clock.SetUtcNow(past.AddHours(1));
        Assert.True((await rig.CheckAsync(past, past.AddHours(1))).IsFree);
    }

    [Theory]
    [InlineData(AllocationStatus.Running)]
    [InlineData(AllocationStatus.Cleaning)]
    public async Task Started_allocation_keeps_blocking_past_its_end_until_released(AllocationStatus status)
    {
        await using var rig = await Rig.CreateAsync();
        var past = Now.AddHours(-5);
        rig.AddAllocation(past, past.AddHours(1), status: status);
        await rig.SaveAsync();

        Assert.False((await rig.CheckAsync(past, past.AddHours(1))).IsFree);
        Assert.False((await rig.CheckAsync(Now.AddHours(1), Now.AddHours(2))).IsFree);
        Assert.False((await rig.CheckAsync(Now.AddDays(10), Now.AddDays(10).AddHours(1))).IsFree);
        // Before it began it does not block.
        Assert.True((await rig.CheckAsync(past.AddHours(-3), past.AddHours(-2))).IsFree);
    }

    [Fact]
    public async Task Allocations_of_other_sources_never_expire()
    {
        await using var rig = await Rig.CreateAsync();
        var past = Now.AddHours(-5);
        rig.AddAllocation(past, past.AddHours(1), source: AssetAllocationSource.CatalogOrder);
        await rig.SaveAsync();

        Assert.False((await rig.CheckAsync(past, past.AddHours(1))).IsFree);
    }

    [Fact]
    public async Task Window_longer_than_30_days_is_rejected_and_exactly_30_days_is_accepted()
    {
        await using var rig = await Rig.CreateAsync();
        var asset = await rig.ResolveAsync();

        var ok = rig.Occupancy.ValidateWindow(asset, AssetAllocationKind.Use, Base, Base.AddDays(30), 0, 0);
        Assert.Equal(Base.AddDays(30), ok.WorkEnd);

        var ex = Assert.Throws<PlanningException>(() => rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Use, Base, Base.AddDays(30).AddMinutes(1), 0, 0));
        Assert.Equal(PlanningException.InvalidAllocationCode, ex.Code);
        Assert.Equal(400, ex.StatusCode);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(60, -60)]
    public async Task Empty_or_inverted_window_is_rejected(int startMinutes, int endMinutes)
    {
        await using var rig = await Rig.CreateAsync();
        var asset = await rig.ResolveAsync();

        var ex = Assert.Throws<PlanningException>(() => rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Use, Base.AddMinutes(startMinutes), Base.AddMinutes(endMinutes), 0, 0));
        Assert.Equal(PlanningException.InvalidAllocationCode, ex.Code);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1441)]
    public async Task Buffers_outside_the_allowed_range_are_rejected(int prep, int cleanup)
    {
        await using var rig = await Rig.CreateAsync();
        var asset = await rig.ResolveAsync();

        var ex = Assert.Throws<PlanningException>(() => rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Use, Base, Base.AddMinutes(60), prep, cleanup));
        Assert.Equal(PlanningException.InvalidAllocationCode, ex.Code);
    }

    [Fact]
    public async Task Start_within_hours_policy_flags_a_start_outside_hours_but_lets_the_job_finish_after_closing()
    {
        // Monday to Friday, 08:00-18:00 in Sao Paulo = 11:00-21:00Z.
        await using var rig = await Rig.CreateAsync(AssetHoursPolicy.StartWithinHours, weekdayHours: true);
        var asset = await rig.ResolveAsync();
        var friday = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

        var lateFinish = rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Use, friday.AddHours(20), friday.AddHours(30), 0, 0);
        Assert.Null(lateFinish.HoursViolation);

        var earlyStart = rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Use, friday.AddHours(9), friday.AddHours(12), 0, 0);
        Assert.Equal(AssetWorkingHoursRules.StartOutsideHours, earlyStart.HoursViolation);

        var saturday = rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Use, friday.AddHours(36), friday.AddHours(38), 0, 0);
        Assert.Equal(AssetWorkingHoursRules.StartOutsideHours, saturday.HoursViolation);
    }

    [Fact]
    public async Task Within_hours_policy_rejects_a_window_that_leaves_working_hours()
    {
        await using var rig = await Rig.CreateAsync(AssetHoursPolicy.WithinHours, weekdayHours: true);
        var asset = await rig.ResolveAsync();
        var friday = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Use, friday.AddHours(12), friday.AddHours(20), 0, 0).HoursViolation);
        Assert.Equal(
            AssetWorkingHoursRules.OutsideHours,
            rig.Occupancy.ValidateWindow(
                asset, AssetAllocationKind.Use, friday.AddHours(20), friday.AddHours(22), 0, 0).HoursViolation);
    }

    [Fact]
    public async Task Unavailable_block_is_not_subject_to_working_hours()
    {
        await using var rig = await Rig.CreateAsync(AssetHoursPolicy.WithinHours, weekdayHours: true);
        var asset = await rig.ResolveAsync();
        var saturday = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

        var window = rig.Occupancy.ValidateWindow(
            asset, AssetAllocationKind.Unavailable, saturday, saturday.AddHours(24), 0, 0);

        Assert.Null(window.HoursViolation);
    }

    [Fact]
    public async Task Asset_of_another_tenant_or_unknown_asset_is_not_found()
    {
        await using var rig = await Rig.CreateAsync();

        var unknown = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Occupancy.ResolvePlannableAssetAsync(rig.Seed.TenantId, Guid.NewGuid(), default));
        Assert.Equal(404, unknown.StatusCode);

        var foreignTenant = await Assert.ThrowsAsync<PlanningException>(
            () => rig.Occupancy.ResolvePlannableAssetAsync(Guid.NewGuid(), rig.Seed.AssetId, default));
        Assert.Equal(PlanningException.AssetNotFoundCode, foreignTenant.Code);
    }

    [Fact]
    public async Task Asset_without_a_plannable_profile_is_not_plannable()
    {
        await using var rig = await Rig.CreateAsync();
        var profile = rig.Db.AssetPlanningProfiles.Single(p => p.AssetId == rig.Seed.AssetId);
        profile.IsPlannable = false;
        await rig.SaveAsync();

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.ResolveAsync());

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(PlanningException.AssetNotPlannableCode, ex.Code);
    }

    [Fact]
    public async Task Inactive_asset_is_not_plannable()
    {
        await using var rig = await Rig.CreateAsync();
        var asset = rig.Db.Assets.Single(a => a.Id == rig.Seed.AssetId);
        asset.Status = AssetStatus.Maintenance;
        await rig.SaveAsync();

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.ResolveAsync());

        Assert.Equal(PlanningException.AssetNotPlannableCode, ex.Code);
    }

    [Fact]
    public async Task Asset_closed_all_week_gets_asset_not_plannable_not_a_generic_hours_error()
    {
        // L7: a policy that needs hours with no hours means the asset can no longer be planned.
        await using var rig = await Rig.CreateAsync(AssetHoursPolicy.StartWithinHours, weekdayHours: false);

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.ResolveAsync());

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(PlanningException.AssetNotPlannableCode, ex.Code);
        Assert.Contains("closed all week", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rental_backed_asset_is_rejected_with_asset_not_plannable_while_rentals_is_active()
    {
        await using var rig = await Rig.CreateAsync(withRentalsModule: true);
        var rental = new RentalAsset
        {
            TenantId = rig.Seed.TenantId,
            AssetId = rig.Seed.AssetId,
            Type = RentalAssetType.Good,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours,
        };
        rig.Db.RentalAssets.Add(rental);
        await rig.SaveAsync();

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.ResolveAsync());
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal(PlanningException.AssetNotPlannableCode, ex.Code);
        Assert.Contains("rental-backed", ex.Message, StringComparison.Ordinal);

        // With rentals off the rental configuration is inert and the asset is planned normally.
        var rentalsOff = new AssetOccupancyService(rig.Db, new FixedModuleAccessor(), rig.Clock);
        var asset = await rentalsOff.ResolvePlannableAssetAsync(rig.Seed.TenantId, rig.Seed.AssetId, default);
        Assert.Equal(rig.Seed.AssetId, asset.AssetId);
    }

    [Fact]
    public async Task Resolved_asset_carries_the_profile_hours_zone_and_capacity()
    {
        await using var rig = await Rig.CreateAsync(AssetHoursPolicy.WithinHours, capacity: 2, weekdayHours: true);

        var asset = await rig.ResolveAsync();

        Assert.Equal(2, asset.Capacity);
        Assert.Equal(AssetHoursPolicy.WithinHours, asset.HoursPolicy);
        Assert.Equal(5, asset.WorkingHours.Count);
        Assert.Equal("America/Sao_Paulo", asset.TimeZoneId);
        Assert.Equal(rig.Seed.UnitId, asset.UnitId);
    }

    internal sealed class Rig : IAsyncDisposable
    {
        private Rig(AppDbContext db, PlanningTenantSeed seed, TestTimeProvider clock, string[] modules)
        {
            Db = db;
            Seed = seed;
            Clock = clock;
            Occupancy = new AssetOccupancyService(db, new FixedModuleAccessor(modules), clock);
        }

        public AppDbContext Db { get; }

        public PlanningTenantSeed Seed { get; }

        public TestTimeProvider Clock { get; }

        public AssetOccupancyService Occupancy { get; }

        public static async Task<Rig> CreateAsync(
            AssetHoursPolicy policy = AssetHoursPolicy.Anytime,
            int capacity = 1,
            bool weekdayHours = false,
            bool withRentalsModule = false,
            int prepMinutes = 0,
            int cleanupMinutes = 0)
        {
            var provider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(provider);
            var seed = PlanningTestData.AddTenant(
                db,
                policy: policy,
                capacity: capacity,
                withRentalsModule: withRentalsModule,
                prepMinutes: prepMinutes,
                cleanupMinutes: cleanupMinutes);
            provider.TenantId = seed.TenantId;
            if (weekdayHours)
            {
                foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
                {
                    db.AssetWorkingHours.Add(PlanningTestData.Hours(seed.TenantId, seed.AssetId, day, 8, 18));
                }
            }

            await db.SaveChangesAsync();
            var modules = withRentalsModule
                ? new[] { PlatformModules.ResourcePlanning, PlatformModules.Rentals }
                : new[] { PlatformModules.ResourcePlanning };
            return new Rig(db, seed, new TestTimeProvider(Now), modules);
        }

        public Task<PlannableAsset> ResolveAsync() =>
            Occupancy.ResolvePlannableAssetAsync(Seed.TenantId, Seed.AssetId, default);

        public Task SaveAsync() => Db.SaveChangesAsync();

        public AssetAllocation AddAllocation(
            DateTimeOffset workStart,
            DateTimeOffset workEnd,
            AllocationStatus status = AllocationStatus.Scheduled,
            AssetAllocationKind kind = AssetAllocationKind.Use,
            AssetAllocationSource source = AssetAllocationSource.Manual,
            int quantity = 1,
            int prepMinutes = 0,
            int cleanupMinutes = 0,
            Guid? assetId = null)
        {
            var allocation = new AssetAllocation
            {
                TenantId = Seed.TenantId,
                AssetId = assetId ?? Seed.AssetId,
                UnitId = Seed.UnitId,
                Kind = kind,
                Title = "Existing job",
                Quantity = quantity,
                PlannedWorkStart = workStart,
                PlannedWorkEnd = workEnd,
                OccupiedStart = workStart.AddMinutes(-prepMinutes),
                OccupiedEnd = workEnd.AddMinutes(cleanupMinutes),
                TimeZoneId = "America/Sao_Paulo",
                Status = status,
                Source = source,
            };
            Db.AssetAllocations.Add(allocation);
            return allocation;
        }

        public async Task<AvailabilityResult> CheckAsync(
            DateTimeOffset workStart,
            DateTimeOffset workEnd,
            int prepMinutes = 0,
            int cleanupMinutes = 0,
            AssetAllocationKind kind = AssetAllocationKind.Use)
        {
            var asset = await ResolveAsync();
            var window = Occupancy.ValidateWindow(asset, kind, workStart, workEnd, prepMinutes, cleanupMinutes);
            return await Occupancy.CheckAvailabilityAsync(
                Seed.TenantId, asset, kind, window, excludeAllocationId: null, default);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
