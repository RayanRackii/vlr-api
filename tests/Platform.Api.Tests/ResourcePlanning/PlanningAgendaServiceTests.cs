using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Content and validation of the planning agenda read. The constant number of round trips is
/// asserted on PostgreSQL in <c>PlanningAllocationConcurrencyTests</c>, where commands are real.
/// </summary>
public sealed class PlanningAgendaServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(7);

    [Fact]
    public async Task Agenda_lists_plannable_assets_with_hours_and_the_allocations_overlapping_the_range()
    {
        await using var rig = await AssetAllocationServiceTests.Rig.CreateAsync(AssetHoursPolicy.WithinHours, weekdayHours: true);
        var inside = Add(rig, From.AddDays(1), From.AddDays(1).AddHours(2), "Inside");
        var before = Add(rig, From.AddDays(-3), From.AddDays(-3).AddHours(2), "Before range");
        var after = Add(rig, To.AddDays(1), To.AddDays(1).AddHours(2), "After range");
        var cancelled = Add(rig, From.AddDays(2), From.AddDays(2).AddHours(1), "Cancelled", AllocationStatus.Cancelled);
        await rig.Db.SaveChangesAsync();

        var agenda = await rig.Agenda.GetAgendaAsync(From, To, null, null, default);

        Assert.Equal(From, agenda.From);
        Assert.Equal(To, agenda.To);
        Assert.Equal(["P1", "P2"], agenda.Resources.Select(r => r.Tag).ToArray());
        var printer = agenda.Resources.Single(r => r.AssetId == rig.Seed.AssetId);
        Assert.Equal("WithinHours", printer.HoursPolicy);
        Assert.Equal(5, printer.WorkingHours.Count);
        Assert.Equal("08:00", printer.WorkingHours[0].OpenTime);
        Assert.Equal("America/Sao_Paulo", printer.TimeZoneId);
        Assert.False(printer.IsRentalBacked);
        Assert.Equal(["Inside"], agenda.Items.Select(i => i.Title).ToArray());
        Assert.Equal("Printer A", agenda.Items.Single().AssetName);
        Assert.Empty(agenda.ExternalBusy);
        Assert.NotEqual(Guid.Empty, inside.Id);
        Assert.DoesNotContain(agenda.Items, i => i.Id == before.Id || i.Id == after.Id || i.Id == cancelled.Id);
    }

    [Fact]
    public async Task Edge_of_the_range_is_half_open()
    {
        await using var rig = await AssetAllocationServiceTests.Rig.CreateAsync();
        Add(rig, From.AddHours(-2), From, "Ends at from");
        Add(rig, To, To.AddHours(2), "Starts at to");
        Add(rig, From.AddHours(-1), From.AddHours(1), "Crosses from");
        await rig.Db.SaveChangesAsync();

        var agenda = await rig.Agenda.GetAgendaAsync(From, To, null, null, default);

        Assert.Equal(["Crosses from"], agenda.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task Started_allocation_past_its_end_is_still_listed_and_flagged_overdue_while_expired_ones_are_marked()
    {
        await using var rig = await AssetAllocationServiceTests.Rig.CreateAsync();
        var oldRunning = Add(rig, From.AddDays(-9), From.AddDays(-9).AddHours(1), "Forgotten", AllocationStatus.Running);
        var oldScheduled = Add(rig, From.AddDays(-9), From.AddDays(-9).AddHours(2), "Never started");
        await rig.Db.SaveChangesAsync();
        rig.Clock.SetUtcNow(From.AddDays(1));

        var agenda = await rig.Agenda.GetAgendaAsync(From, To, null, null, default);

        var forgotten = agenda.Items.Single();
        Assert.Equal(oldRunning.Id, forgotten.Id);
        Assert.True(forgotten.IsOverdue);
        Assert.True(forgotten.IsBlocking);

        // An expired one outside the range is not listed; inside the range it is, but not blocking.
        var wide = await rig.Agenda.GetAgendaAsync(From.AddDays(-10), From.AddDays(-5), null, null, default);
        var expired = wide.Items.Single(i => i.Id == oldScheduled.Id);
        Assert.True(expired.IsExpired);
        Assert.False(expired.IsBlocking);
    }

    [Fact]
    public async Task Not_plannable_deleted_and_other_tenant_assets_are_not_resources()
    {
        await using var rig = await AssetAllocationServiceTests.Rig.CreateAsync();
        // A second tenant with plannable assets and allocations of its own.
        var foreign = PlanningTestData.AddTenant(rig.Db, "Other Shop");
        rig.Db.AssetAllocations.Add(new AssetAllocation
        {
            TenantId = foreign.TenantId,
            AssetId = foreign.AssetId,
            UnitId = foreign.UnitId,
            Title = "Foreign job",
            PlannedWorkStart = From.AddDays(1),
            PlannedWorkEnd = From.AddDays(1).AddHours(1),
            OccupiedStart = From.AddDays(1),
            OccupiedEnd = From.AddDays(1).AddHours(1),
            TimeZoneId = "America/Sao_Paulo",
        });
        rig.Db.AssetPlanningProfiles.Single(p => p.AssetId == rig.Seed.SecondAssetId).IsPlannable = false;
        await rig.Db.SaveChangesAsync();

        var plannableOnly = await rig.Agenda.GetAgendaAsync(From, To, null, null, default);
        Assert.Equal([rig.Seed.AssetId], plannableOnly.Resources.Select(r => r.AssetId).ToArray());
        Assert.Empty(plannableOnly.Items);

        rig.Db.Assets.Single(a => a.Id == rig.Seed.AssetId).ScheduledDeletionAt = Now.AddDays(3);
        await rig.Db.SaveChangesAsync();
        Assert.Empty((await rig.Agenda.GetAgendaAsync(From, To, null, null, default)).Resources);
    }

    [Fact]
    public async Task Filters_by_asset_ids_and_unit()
    {
        await using var rig = await AssetAllocationServiceTests.Rig.CreateAsync();
        Add(rig, From.AddDays(1), From.AddDays(1).AddHours(1), "On A");
        Add(rig, From.AddDays(1), From.AddDays(1).AddHours(1), "On B", assetId: rig.Seed.SecondAssetId);
        await rig.Db.SaveChangesAsync();

        var onlyB = await rig.Agenda.GetAgendaAsync(From, To, [rig.Seed.SecondAssetId], null, default);
        Assert.Equal([rig.Seed.SecondAssetId], onlyB.Resources.Select(r => r.AssetId).ToArray());
        Assert.Equal(["On B"], onlyB.Items.Select(i => i.Title).ToArray());

        var otherUnit = await rig.Agenda.GetAgendaAsync(From, To, null, Guid.NewGuid(), default);
        Assert.Empty(otherUnit.Resources);
        Assert.Empty(otherUnit.Items);

        var sameUnit = await rig.Agenda.GetAgendaAsync(From, To, null, rig.Seed.UnitId, default);
        Assert.Equal(2, sameUnit.Resources.Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 43)]
    public async Task Invalid_range_is_rejected_with_400(int fromOffsetDays, int toOffsetDays)
    {
        await using var rig = await AssetAllocationServiceTests.Rig.CreateAsync();

        var ex = await Assert.ThrowsAsync<PlanningException>(() => rig.Agenda.GetAgendaAsync(
            From.AddDays(fromOffsetDays), From.AddDays(toOffsetDays), null, null, default));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(PlanningException.InvalidRangeCode, ex.Code);
    }

    [Fact]
    public async Task Range_of_exactly_42_days_is_accepted_and_year_bounds_are_enforced()
    {
        await using var rig = await AssetAllocationServiceTests.Rig.CreateAsync();

        await rig.Agenda.GetAgendaAsync(From, From.AddDays(42), null, null, default);

        await Assert.ThrowsAsync<PlanningException>(() => rig.Agenda.GetAgendaAsync(
            new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(1999, 1, 2, 0, 0, 0, TimeSpan.Zero), null, null, default));
        await Assert.ThrowsAsync<PlanningException>(() => rig.Agenda.GetAgendaAsync(
            new DateTimeOffset(3000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(3000, 1, 2, 0, 0, 0, TimeSpan.Zero), null, null, default));
    }

    private static AssetAllocation Add(
        AssetAllocationServiceTests.Rig rig,
        DateTimeOffset start,
        DateTimeOffset end,
        string title,
        AllocationStatus status = AllocationStatus.Scheduled,
        Guid? assetId = null)
    {
        var allocation = new AssetAllocation
        {
            TenantId = rig.Seed.TenantId,
            AssetId = assetId ?? rig.Seed.AssetId,
            UnitId = rig.Seed.UnitId,
            Title = title,
            PlannedWorkStart = start,
            PlannedWorkEnd = end,
            OccupiedStart = start,
            OccupiedEnd = end,
            TimeZoneId = "America/Sao_Paulo",
            Status = status,
        };
        rig.Db.AssetAllocations.Add(allocation);
        return allocation;
    }
}
