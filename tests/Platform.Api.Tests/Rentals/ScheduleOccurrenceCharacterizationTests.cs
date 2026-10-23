using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

/// <summary>
/// Characterization of the schedule write paths split out of ScheduleService (step 15):
/// cancel slot, weekly rule, and daily occurrence MakeUnavailable / RestoreWeeklyDefault.
/// Pins current behavior; not a spec.
/// </summary>
public sealed class ScheduleOccurrenceCharacterizationTests
{
    private static readonly DateOnly PreviousTuesday = new(2026, 8, 18);
    private static readonly DateOnly Tuesday = new(2026, 8, 25);
    private static readonly DateOnly NextTuesday = new(2026, 9, 1);
    private static readonly DateOnly TuesdayAfterNext = new(2026, 9, 8);
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly TwentyTwo = new(22, 0);

    [Fact]
    public async Task CancelSlot_marks_available_slot_cancelled()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var slot = await service.UpsertSlotAsync(Slot(harness, kinds.Open.Id, Tuesday), CancellationToken.None);

        await service.CancelSlotAsync(slot.Id, CancellationToken.None);

        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == slot.Id);
        Assert.Equal(SlotStatus.Cancelled, persisted.Status);
    }

    [Fact]
    public async Task CancelSlot_booked_slot_throws_and_stays_booked()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var slot = await service.UpsertSlotAsync(Slot(harness, kinds.Open.Id, Tuesday), CancellationToken.None);
        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == slot.Id);
        persisted.MarkBooked(Guid.NewGuid());
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelSlotAsync(slot.Id, CancellationToken.None));

        Assert.Equal(SlotStatus.Booked, persisted.Status);
    }

    [Fact]
    public async Task CancelSlot_unknown_id_throws_not_found()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CancelSlotAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ApplyWeeklyRule_creates_grid_then_reapply_skips_exact_matches()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var request = new ApplyWeeklyRuleRequestDto
        {
            RentalAssetIds = [harness.RentalAssetId],
            DaysOfWeek = [DayOfWeek.Tuesday, DayOfWeek.Wednesday],
            OpenTime = Eight,
            CloseTime = Ten,
            SlotMinutes = 60,
            OccupancyKindId = kinds.Open.Id,
            Label = "  Aula  ",
        };

        var first = await service.ApplyWeeklyRuleAsync(request, CancellationToken.None);
        var second = await service.ApplyWeeklyRuleAsync(request, CancellationToken.None);

        Assert.Equal((4, 0, 0), (first.Created, first.Updated, first.Skipped));
        Assert.Equal((0, 0, 4), (second.Created, second.Updated, second.Skipped));
        var templates = await service.ListTemplatesAsync(harness.RentalAssetId, CancellationToken.None);
        Assert.Equal(4, templates.Count);
        Assert.All(templates, t => Assert.Equal("Aula", t.Label));
        Assert.Equal(
            [Eight, new TimeOnly(9, 0)],
            templates.Where(t => t.DayOfWeek == DayOfWeek.Tuesday).Select(t => t.StartTime).Order());
    }

    [Fact]
    public async Task ApplyWeeklyRule_rejects_slot_longer_than_open_interval()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyWeeklyRuleAsync(
            new ApplyWeeklyRuleRequestDto
            {
                RentalAssetIds = [harness.RentalAssetId],
                DaysOfWeek = [DayOfWeek.Tuesday],
                OpenTime = Eight,
                CloseTime = Ten,
                SlotMinutes = 180,
                OccupancyKindId = kinds.Open.Id,
            },
            CancellationToken.None));

        Assert.Empty(await service.ListTemplatesAsync(harness.RentalAssetId, CancellationToken.None));
    }

    [Fact]
    public async Task MakeUnavailable_only_this_day_without_slot_creates_cancelled_open_tombstone()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();

        var result = await service.ApplyDailyOccurrenceAsync(
            Occurrence(harness, Tuesday, DailyOccurrenceAction.MakeUnavailable, OccurrenceEditScope.OnlyThisDay),
            CancellationToken.None);

        var tombstone = await harness.Db.Slots.SingleAsync();
        Assert.Equal(result.Id, tombstone.Id);
        Assert.Equal(SlotStatus.Cancelled, tombstone.Status);
        Assert.Equal(kinds.Open.Id, tombstone.OccupancyKindId);
        Assert.Null(tombstone.Label);
        Assert.Null(tombstone.SourceTemplateId);
        Assert.Equal((Tuesday, Eight, TwentyTwo), (tombstone.Date, tombstone.StartTime, tombstone.EndTime));
    }

    [Fact]
    public async Task MakeUnavailable_only_this_day_cancels_that_slot_and_keeps_weekly_rule()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var template = await service.CreateTemplateAsync(OpenTemplate(harness, kinds.Open.Id), CancellationToken.None);
        await PublishAsync(service, harness, Tuesday, NextTuesday);
        var slot = await harness.Db.Slots.SingleAsync(s => s.Date == Tuesday);

        var result = await service.ApplyDailyOccurrenceAsync(
            Occurrence(harness, Tuesday, DailyOccurrenceAction.MakeUnavailable, OccurrenceEditScope.OnlyThisDay, slot.Id),
            CancellationToken.None);

        Assert.Equal(slot.Id, result.Id);
        Assert.Equal(SlotStatus.Cancelled, result.Status);
        Assert.Equal(SlotOccurrenceSource.DailyOverride, result.Source);
        Assert.True((await harness.Db.ScheduleTemplates.SingleAsync(t => t.Id == template.Id)).IsActive);
        Assert.Equal(SlotStatus.Available, (await harness.Db.Slots.SingleAsync(s => s.Date == NextTuesday)).Status);
    }

    [Fact]
    public async Task MakeUnavailable_entire_recurrence_deactivates_rule_and_cancels_future_unbooked_slots()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var template = await service.CreateTemplateAsync(OpenTemplate(harness, kinds.Open.Id), CancellationToken.None);
        await PublishAsync(service, harness, PreviousTuesday, Tuesday, NextTuesday, TuesdayAfterNext);
        var booked = await harness.Db.Slots.SingleAsync(s => s.Date == TuesdayAfterNext);
        booked.MarkBooked(Guid.NewGuid());
        await harness.Db.SaveChangesAsync();
        var slot = await harness.Db.Slots.SingleAsync(s => s.Date == Tuesday);

        var result = await service.ApplyDailyOccurrenceAsync(
            Occurrence(harness, Tuesday, DailyOccurrenceAction.MakeUnavailable, OccurrenceEditScope.EntireRecurrence, slot.Id),
            CancellationToken.None);

        Assert.Equal(slot.Id, result.Id);
        Assert.Equal(SlotStatus.Cancelled, result.Status);
        Assert.Equal(SlotOccurrenceSource.DailyOverride, result.Source);
        Assert.False((await harness.Db.ScheduleTemplates.SingleAsync(t => t.Id == template.Id)).IsActive);
        var byDate = await harness.Db.Slots.ToDictionaryAsync(s => s.Date, s => s.Status);
        Assert.Equal(SlotStatus.Available, byDate[PreviousTuesday]);
        Assert.Equal(SlotStatus.Cancelled, byDate[Tuesday]);
        Assert.Equal(SlotStatus.Cancelled, byDate[NextTuesday]);
        Assert.Equal(SlotStatus.Booked, byDate[TuesdayAfterNext]);
    }

    [Fact]
    public async Task MakeUnavailable_entire_recurrence_without_weekly_rule_throws()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var request = Occurrence(harness, Tuesday, DailyOccurrenceAction.MakeUnavailable, OccurrenceEditScope.EntireRecurrence)
            with { OccupancyKindId = kinds.Open.Id };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyDailyOccurrenceAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task RestoreWeeklyDefault_only_this_day_revives_tombstone_from_template()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var template = await service.CreateTemplateAsync(OpenTemplate(harness, kinds.Open.Id), CancellationToken.None);
        var tombstone = await service.ApplyDailyOccurrenceAsync(
            Occurrence(harness, Tuesday, DailyOccurrenceAction.MakeUnavailable, OccurrenceEditScope.OnlyThisDay),
            CancellationToken.None);

        var result = await service.ApplyDailyOccurrenceAsync(
            Occurrence(harness, Tuesday, DailyOccurrenceAction.RestoreWeeklyDefault, OccurrenceEditScope.OnlyThisDay, tombstone.Id),
            CancellationToken.None);

        Assert.Equal(tombstone.Id, result.Id);
        Assert.Equal(SlotStatus.Available, result.Status);
        Assert.Equal(SlotOccurrenceSource.WeeklyDefault, result.Source);
        Assert.Equal(template.Id, result.SourceTemplateId);
        Assert.Equal(kinds.Open.Id, result.OccupancyKindId);
        Assert.False(result.IsDerived);
        Assert.Single(harness.Db.Slots);
    }

    [Fact]
    public async Task RestoreWeeklyDefault_only_this_day_without_weekly_rule_throws_on_slot_grid()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var request = Occurrence(harness, Tuesday, DailyOccurrenceAction.RestoreWeeklyDefault, OccurrenceEditScope.OnlyThisDay)
            with { OccupancyKindId = kinds.Open.Id };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyDailyOccurrenceAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task RestoreWeeklyDefault_entire_recurrence_is_rejected_without_mutation()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var template = await service.CreateTemplateAsync(OpenTemplate(harness, kinds.Open.Id), CancellationToken.None);
        await PublishAsync(service, harness, Tuesday);
        var slot = await harness.Db.Slots.SingleAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyDailyOccurrenceAsync(
            Occurrence(harness, Tuesday, DailyOccurrenceAction.RestoreWeeklyDefault, OccurrenceEditScope.EntireRecurrence, slot.Id),
            CancellationToken.None));

        Assert.Equal(SlotStatus.Available, slot.Status);
        Assert.True((await harness.Db.ScheduleTemplates.SingleAsync(t => t.Id == template.Id)).IsActive);
    }

    private static async Task PublishAsync(
        Platform.Api.Modules.Rentals.Services.ScheduleService service,
        ScheduleOverlapHarness harness,
        params DateOnly[] dates)
    {
        foreach (var date in dates)
        {
            Assert.Equal(1, await service.PublishDayAsync(
                new PublishDayRequestDto { Date = date, RentalAssetId = harness.RentalAssetId },
                CancellationToken.None));
        }
    }

    private static UpsertScheduleTemplateRequestDto OpenTemplate(ScheduleOverlapHarness harness, Guid openKindId) =>
        new()
        {
            RentalAssetId = harness.RentalAssetId,
            DayOfWeek = DayOfWeek.Tuesday,
            StartTime = Eight,
            EndTime = TwentyTwo,
            OccupancyKindId = openKindId,
        };

    private static UpsertSlotRequestDto Slot(ScheduleOverlapHarness harness, Guid kindId, DateOnly date) =>
        new()
        {
            RentalAssetId = harness.RentalAssetId,
            Date = date,
            StartTime = Eight,
            EndTime = TwentyTwo,
            OccupancyKindId = kindId,
        };

    private static ApplyDailyOccurrenceRequestDto Occurrence(
        ScheduleOverlapHarness harness,
        DateOnly date,
        DailyOccurrenceAction action,
        OccurrenceEditScope scope,
        Guid? slotId = null) =>
        new()
        {
            SlotId = slotId,
            RentalAssetId = harness.RentalAssetId,
            Date = date,
            StartTime = Eight,
            EndTime = TwentyTwo,
            Action = action,
            Scope = scope,
        };
}
