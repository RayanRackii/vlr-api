using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class ScheduleTemplateOverlapTests
{
    private static readonly DateOnly Tuesday = new(2026, 8, 25);
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Eighteen = new(18, 0);
    private static readonly TimeOnly Nineteen = new(19, 0);
    private static readonly TimeOnly TwentyTwo = new(22, 0);

    [Fact]
    public async Task Create_open_and_lesson_overlap_lesson_wins_unpublished_day()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();

        await service.CreateTemplateAsync(Template(harness, kinds.Open.Id, Eight, TwentyTwo), CancellationToken.None);
        await service.CreateTemplateAsync(Template(harness, kinds.Lesson.Id, Eighteen, Nineteen), CancellationToken.None);

        var templates = await service.ListTemplatesAsync(harness.RentalAssetId, CancellationToken.None);
        Assert.Equal(2, templates.Count);

        var day = await service.GetDayAsync(
            Tuesday, harness.RentalAssetId, customerFacing: false, CancellationToken.None);

        AssertLessonWinsEvening(day);
    }

    [Fact]
    public async Task Create_open_and_closed_overlap_closed_wins_overlap()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();

        await service.CreateTemplateAsync(Template(harness, kinds.Open.Id, Eight, TwentyTwo), CancellationToken.None);
        await service.CreateTemplateAsync(Template(harness, kinds.Closed.Id, Eight, TwentyTwo), CancellationToken.None);

        var day = await service.GetDayAsync(
            Tuesday, harness.RentalAssetId, customerFacing: false, CancellationToken.None);

        var window = Assert.Single(day.Slots);
        Assert.Equal("closed", window.OccupancyKindKey);
        Assert.Equal(Eight, window.StartTime);
        Assert.Equal(TwentyTwo, window.EndTime);
        Assert.True(window.IsDerived);
    }

    [Fact]
    public async Task CreateTemplate_exact_duplicate_is_rejected_without_extra_row()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var request = Template(harness, kinds.Open.Id, Eight, TwentyTwo);

        await service.CreateTemplateAsync(request, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateTemplateAsync(request, CancellationToken.None));

        var templates = await service.ListTemplatesAsync(harness.RentalAssetId, CancellationToken.None);
        Assert.Single(templates);
    }

    [Fact]
    public async Task CreateTemplate_same_kind_overlap_is_rejected()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();

        await service.CreateTemplateAsync(Template(harness, kinds.Open.Id, Eight, TwentyTwo), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateTemplateAsync(
                Template(harness, kinds.Open.Id, Eighteen, Nineteen),
                CancellationToken.None));

        Assert.Single(await service.ListTemplatesAsync(harness.RentalAssetId, CancellationToken.None));
    }

    [Fact]
    public async Task ApplyWeeklyRule_same_start_different_kinds_persists_both()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();

        await service.CreateTemplateAsync(
            Template(harness, kinds.Open.Id, Eight, new TimeOnly(9, 0)),
            CancellationToken.None);

        var result = await service.ApplyWeeklyRuleAsync(
            new ApplyWeeklyRuleRequestDto
            {
                RentalAssetIds = [harness.RentalAssetId],
                DaysOfWeek = [DayOfWeek.Tuesday],
                OpenTime = Eight,
                CloseTime = new TimeOnly(9, 0),
                SlotMinutes = 60,
                OccupancyKindId = kinds.Lesson.Id,
            },
            CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);

        var templates = await service.ListTemplatesAsync(harness.RentalAssetId, CancellationToken.None);
        Assert.Equal(2, templates.Count);
        Assert.Contains(templates, t => t.OccupancyKindId == kinds.Open.Id && t.StartTime == Eight);
        Assert.Contains(templates, t => t.OccupancyKindId == kinds.Lesson.Id && t.StartTime == Eight);
    }

    [Fact]
    public async Task PublishDay_preserves_existing_booked_slot_and_fills_lesson_gap()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();

        await service.CreateTemplateAsync(Template(harness, kinds.Open.Id, Eight, TwentyTwo), CancellationToken.None);
        var morning = await service.UpsertSlotAsync(
            new UpsertSlotRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                Date = Tuesday,
                StartTime = Eight,
                EndTime = new TimeOnly(9, 0),
                OccupancyKindId = kinds.Open.Id,
            },
            CancellationToken.None);

        var persisted = await harness.Db.Slots.FirstAsync(s => s.Id == morning.Id);
        persisted.MarkBooked(Guid.NewGuid());
        await harness.Db.SaveChangesAsync();

        await service.CreateTemplateAsync(Template(harness, kinds.Lesson.Id, Eighteen, Nineteen), CancellationToken.None);

        var created = await service.PublishDayAsync(
            new PublishDayRequestDto
            {
                Date = Tuesday,
                RentalAssetId = harness.RentalAssetId,
            },
            CancellationToken.None);

        Assert.Equal(1, created);

        var day = await service.GetDayAsync(
            Tuesday, harness.RentalAssetId, customerFacing: false, CancellationToken.None);
        var booked = Assert.Single(day.Slots, s => s.Id == morning.Id);
        Assert.Equal(SlotStatus.Booked, booked.Status);
        Assert.Equal(Eight, booked.StartTime);
        Assert.Equal(new TimeOnly(9, 0), booked.EndTime);

        Assert.Contains(day.Slots, s =>
            !s.IsDerived
            && s.StartTime == Eighteen
            && s.EndTime == Nineteen
            && s.OccupancyKindKey == "lesson");
    }

    [Fact]
    public async Task ApplyWeeklyRule_with_two_seeded_exact_dupes_does_not_throw()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();

        harness.Db.ScheduleTemplates.Add(ExactOpenHour(harness, kinds.Open.Id));
        harness.Db.ScheduleTemplates.Add(ExactOpenHour(harness, kinds.Open.Id));
        await harness.Db.SaveChangesAsync();

        var result = await service.ApplyWeeklyRuleAsync(
            new ApplyWeeklyRuleRequestDto
            {
                RentalAssetIds = [harness.RentalAssetId],
                DaysOfWeek = [DayOfWeek.Tuesday],
                OpenTime = Eight,
                CloseTime = new TimeOnly(9, 0),
                SlotMinutes = 60,
                OccupancyKindId = kinds.Open.Id,
            },
            CancellationToken.None);

        var templates = await service.ListTemplatesAsync(harness.RentalAssetId, CancellationToken.None);
        Assert.True(result.Created + result.Updated + result.Skipped >= 1);
        Assert.Equal(2, templates.Count);
    }

    [Fact]
    public async Task EntireRecurrence_label_update_on_closed_does_not_overwrite_open()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var monday = new DateOnly(2026, 8, 24);

        await service.CreateTemplateAsync(
            Template(harness, kinds.Open.Id, Eight, TwentyTwo, DayOfWeek.Monday),
            CancellationToken.None);
        await service.CreateTemplateAsync(
            Template(harness, kinds.Closed.Id, Eight, TwentyTwo, DayOfWeek.Monday),
            CancellationToken.None);

        await service.ApplyDailyOccurrenceAsync(
            new ApplyDailyOccurrenceRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                Date = monday,
                StartTime = Eight,
                EndTime = TwentyTwo,
                Action = DailyOccurrenceAction.Update,
                Scope = OccurrenceEditScope.EntireRecurrence,
                OccupancyKindId = kinds.Closed.Id,
                Label = "Manutenção",
            },
            CancellationToken.None);

        var templates = await service.ListTemplatesAsync(
            harness.RentalAssetId, CancellationToken.None, dayOfWeek: DayOfWeek.Monday);
        Assert.Equal(2, templates.Count);
        Assert.Contains(templates, t =>
            t.OccupancyKindId == kinds.Open.Id
            && t.StartTime == Eight
            && t.EndTime == TwentyTwo
            && t.Label is null);
        Assert.Contains(templates, t =>
            t.OccupancyKindId == kinds.Closed.Id
            && t.StartTime == Eight
            && t.EndTime == TwentyTwo
            && t.Label == "Manutenção");
    }

    [Fact]
    public async Task EntireRecurrence_converting_open_into_closed_at_same_window_throws()
    {
        await using var harness = await ScheduleOverlapHarness.CreateAsync();
        var service = harness.CreateScheduleService();
        var kinds = await harness.EnsureKindsAsync();
        var monday = new DateOnly(2026, 8, 24);

        await service.CreateTemplateAsync(
            Template(harness, kinds.Open.Id, Eight, TwentyTwo, DayOfWeek.Monday),
            CancellationToken.None);
        await service.CreateTemplateAsync(
            Template(harness, kinds.Closed.Id, Eight, TwentyTwo, DayOfWeek.Monday),
            CancellationToken.None);

        var openSlot = await service.UpsertSlotAsync(
            new UpsertSlotRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                Date = monday,
                StartTime = Eight,
                EndTime = TwentyTwo,
                OccupancyKindId = kinds.Open.Id,
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyDailyOccurrenceAsync(
                new ApplyDailyOccurrenceRequestDto
                {
                    SlotId = openSlot.Id,
                    RentalAssetId = harness.RentalAssetId,
                    Date = monday,
                    StartTime = Eight,
                    EndTime = TwentyTwo,
                    Action = DailyOccurrenceAction.Update,
                    Scope = OccurrenceEditScope.EntireRecurrence,
                    OccupancyKindId = kinds.Closed.Id,
                },
                CancellationToken.None));

        var templates = await service.ListTemplatesAsync(
            harness.RentalAssetId, CancellationToken.None, dayOfWeek: DayOfWeek.Monday);
        Assert.Equal(2, templates.Count);
        Assert.Contains(templates, t => t.OccupancyKindId == kinds.Open.Id);
        Assert.Contains(templates, t => t.OccupancyKindId == kinds.Closed.Id);
    }

    private static void AssertLessonWinsEvening(DayScheduleResponseDto day)
    {
        Assert.Equal(3, day.Slots.Count);
        Assert.Contains(day.Slots, s =>
            s.OccupancyKindKey == "open" && s.StartTime == Eight && s.EndTime == Eighteen);
        Assert.Contains(day.Slots, s =>
            s.OccupancyKindKey == "lesson" && s.StartTime == Eighteen && s.EndTime == Nineteen);
        Assert.Contains(day.Slots, s =>
            s.OccupancyKindKey == "open" && s.StartTime == Nineteen && s.EndTime == TwentyTwo);
        Assert.DoesNotContain(day.Slots, s =>
            s.OccupancyKindKey == "open" && s.StartTime == Eighteen);
    }

    private static UpsertScheduleTemplateRequestDto Template(
        ScheduleOverlapHarness harness,
        Guid occupancyKindId,
        TimeOnly start,
        TimeOnly end,
        DayOfWeek dayOfWeek = DayOfWeek.Tuesday) =>
        new()
        {
            RentalAssetId = harness.RentalAssetId,
            DayOfWeek = dayOfWeek,
            StartTime = start,
            EndTime = end,
            OccupancyKindId = occupancyKindId,
        };

    private static ScheduleTemplate ExactOpenHour(ScheduleOverlapHarness harness, Guid openKindId) =>
        new()
        {
            TenantId = harness.TenantId,
            RentalAssetId = harness.RentalAssetId,
            DayOfWeek = DayOfWeek.Tuesday,
            StartTime = Eight,
            EndTime = new TimeOnly(9, 0),
            OccupancyKindId = openKindId,
            Label = null,
            IsActive = true,
        };
}

internal sealed class ScheduleOverlapHarness : IAsyncDisposable
{
    private ScheduleOverlapHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Guid tenantId,
        Guid rentalAssetId)
    {
        Db = db;
        TenantProvider = tenantProvider;
        TenantId = tenantId;
        RentalAssetId = rentalAssetId;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Guid TenantId { get; }

    public Guid RentalAssetId { get; }

    public static async Task<ScheduleOverlapHarness> CreateAsync()
    {
        var tenantProvider = new FakeTenantProvider();
        var db = InMemoryAppDb.Create(tenantProvider);

        var tenant = new Tenant("Clube Agenda", "77777777000191", subdomain: "clube-agenda");
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = "{}",
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Quadra 1",
            Tag = "Q1",
            Status = AssetStatus.Active,
            IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = tenant.Id,
            AssetId = asset.Id,
            Type = RentalAssetType.Location,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = true,
            SchedulePolicy = SchedulePolicy.SlotGrid,
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0),
            QueueEnabled = false,
        };

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        await db.SaveChangesAsync();

        return new ScheduleOverlapHarness(db, tenantProvider, tenant.Id, rental.Id);
    }

    public async Task<(OccupancyKind Open, OccupancyKind Lesson, OccupancyKind Closed)> EnsureKindsAsync()
    {
        var occupancy = new OccupancyKindService(Db, TenantProvider);
        await occupancy.EnsureDefaultsAsync(CancellationToken.None);
        var kinds = await Db.OccupancyKinds.ToListAsync();
        return (
            kinds.Single(k => k.Key == "open"),
            kinds.Single(k => k.Key == "lesson"),
            kinds.Single(k => k.Key == "closed"));
    }

    public ScheduleService CreateScheduleService() =>
        new(
            Db,
            TenantProvider,
            new OccupancyKindService(Db, TenantProvider),
            new FakeTrialGuard(),
            TestReservationQueue.Create(Db, TenantProvider));

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
