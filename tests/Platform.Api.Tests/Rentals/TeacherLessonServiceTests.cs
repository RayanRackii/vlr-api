using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class TeacherLessonServiceTests
{
    private static readonly DateOnly Tuesday = new(2026, 8, 25);
    private static readonly DateOnly NextTuesday = Tuesday.AddDays(7);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);
    private static readonly TimeOnly Eight = new(8, 0);
    private static readonly TimeOnly Eighteen = new(18, 0);
    private static readonly TimeOnly Nineteen = new(19, 0);
    private static readonly TimeOnly TwentyTwo = new(22, 0);

    [Fact]
    public async Task Create_on_exact_open_segment_materializes_lesson_override()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var created = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);

        Assert.Equal("lesson", created.OccupancyKindKey);
        Assert.Equal(SlotStatus.Available, created.Status);
        Assert.Equal(harness.OpenTemplateId, created.SourceTemplateId);
        Assert.False(created.IsDerived);
        Assert.Equal(1, await harness.Db.Slots.CountAsync());
        Assert.Equal(1, await harness.Db.ScheduleTemplates.CountAsync());
    }

    [Fact]
    public async Task Create_exact_duplicate_is_idempotent()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var first = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        var second = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await harness.Db.Slots.CountAsync());
    }

    [Fact]
    public async Task Create_exact_lesson_with_unverifiable_source_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var lesson = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        var slot = await harness.Db.Slots.SingleAsync();
        slot.SourceTemplateId = Guid.NewGuid();
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        var unchanged = await harness.Db.Slots.SingleAsync();
        Assert.Equal(lesson.Id, unchanged.Id);
        Assert.Equal("lesson", (await harness.Db.OccupancyKinds.SingleAsync(k => k.Id == unchanged.OccupancyKindId)).Key);
        Assert.NotEqual(harness.OpenTemplateId, unchanged.SourceTemplateId);
    }

    [Fact]
    public async Task Create_exact_lesson_with_reservation_link_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var lesson = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        var slot = await harness.Db.Slots.SingleAsync();
        slot.ReservationId = Guid.NewGuid();
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        var unchanged = await harness.Db.Slots.SingleAsync();
        Assert.Equal(lesson.Id, unchanged.Id);
        Assert.Equal(slot.ReservationId, unchanged.ReservationId);
        Assert.Equal("lesson", (await harness.Db.OccupancyKinds.SingleAsync(k => k.Id == unchanged.OccupancyKindId)).Key);
    }

    [Fact]
    public async Task Create_rejects_past_date_before_writing()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Lessons.CreateAsync(
            harness.CreateRequest(date: Tuesday.AddDays(-7)), CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_and_remove_reject_elapsed_interval_today()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var clock = new TestTimeProvider(BrazilTimeZone.AtLocal(Tuesday, new TimeOnly(10, 30)));
        var service = new TeacherLessonService(
            harness.Db, harness.TenantProvider, new FakeTrialGuard(), clock, new TestTeacherLessonScope());

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            harness.CreateRequest(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveAsync(
            new RemoveTeacherLessonRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
            }, CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    public async Task Create_rejects_noncanonical_open_or_lesson_flags(
        bool openBookable,
        bool openBlocks,
        bool lessonBookable,
        bool lessonBlocks)
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var open = await harness.Db.OccupancyKinds.SingleAsync(kind => kind.Key == "open");
        var lesson = await harness.Db.OccupancyKinds.SingleAsync(kind => kind.Key == "lesson");
        open.IsBookableByCustomer = openBookable;
        open.BlocksCapacity = openBlocks;
        lesson.IsBookableByCustomer = lessonBookable;
        lesson.BlocksCapacity = lessonBlocks;
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Lessons.CreateAsync(
            harness.CreateRequest(), CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_duplicate_rejects_when_a_non_open_template_overlaps()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var first = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        var appointment = new OccupancyKind
        {
            TenantId = harness.TenantId,
            Key = "appointment",
            Label = "Appointment",
            IsBookableByCustomer = false,
            BlocksCapacity = false,
            SortOrder = 9,
            IsActive = true,
        };
        harness.Db.OccupancyKinds.Add(appointment);
        await harness.Db.SaveChangesAsync();
        await harness.AddTemplateAsync(appointment.Id, Ten, Eleven);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        var slot = Assert.Single(await harness.Db.Slots.ToListAsync());
        Assert.Equal(first.Id, slot.Id);
        Assert.Equal("lesson", (await harness.Db.OccupancyKinds.SingleAsync(k => k.Id == slot.OccupancyKindId)).Key);
        Assert.Equal(2, await harness.Db.ScheduleTemplates.CountAsync());
    }

    [Fact]
    public async Task Create_missing_canonical_kinds_does_not_seed_defaults_before_rejecting()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync(seedHourlyOpen: false);
        harness.Db.OccupancyKinds.RemoveRange(await harness.Db.OccupancyKinds.ToListAsync());
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        Assert.Empty(await harness.Db.OccupancyKinds.ToListAsync());
        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_converts_exact_published_open_slot_and_preserves_source()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var kinds = await harness.EnsureKindsAsync();
        var published = new Slot
        {
            TenantId = harness.TenantId,
            RentalAssetId = harness.RentalAssetId,
            Date = Tuesday,
            StartTime = Ten,
            EndTime = Eleven,
            OccupancyKindId = kinds.Open.Id,
            Status = SlotStatus.Available,
            SourceTemplateId = harness.OpenTemplateId,
        };
        harness.Db.Slots.Add(published);
        await harness.Db.SaveChangesAsync();

        var created = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);

        Assert.Equal(published.Id, created.Id);
        Assert.Equal("lesson", created.OccupancyKindKey);
        Assert.Equal(harness.OpenTemplateId, created.SourceTemplateId);
        Assert.Equal(1, await harness.Db.Slots.CountAsync());
    }

    [Fact]
    public async Task Create_same_interval_different_label_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        await harness.Lessons.CreateAsync(harness.CreateRequest(label: "Aula A"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(label: "Aula B"), CancellationToken.None));

        var slot = Assert.Single(await harness.Db.Slots.ToListAsync());
        Assert.Equal("Aula A", slot.Label);
    }

    [Fact]
    public async Task Create_split_closed_segment_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync(seedHourlyOpen: false);
        var kinds = await harness.EnsureKindsAsync();
        await harness.AddTemplateAsync(kinds.Open.Id, Eight, TwentyTwo);
        await harness.AddTemplateAsync(kinds.Closed.Id, Eighteen, Nineteen);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(
                harness.CreateRequest(start: Eighteen, end: Nineteen),
                CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
        Assert.Equal(2, await harness.Db.ScheduleTemplates.CountAsync());
    }

    [Fact]
    public async Task Create_exact_open_segment_after_closed_split_succeeds()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync(seedHourlyOpen: false);
        var kinds = await harness.EnsureKindsAsync();
        await harness.AddTemplateAsync(kinds.Open.Id, Eight, TwentyTwo);
        await harness.AddTemplateAsync(kinds.Closed.Id, Eighteen, Nineteen);

        var created = await harness.Lessons.CreateAsync(
            harness.CreateRequest(start: Eight, end: Eighteen),
            CancellationToken.None);

        Assert.Equal("lesson", created.OccupancyKindKey);
        Assert.Equal(Eight, created.StartTime);
        Assert.Equal(Eighteen, created.EndTime);
    }

    [Fact]
    public async Task Create_partial_custom_overlap_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var kinds = await harness.EnsureKindsAsync();
        harness.Db.OccupancyKinds.Add(new OccupancyKind
        {
            TenantId = harness.TenantId,
            Key = "clinic",
            Label = "Clinic",
            IsBookableByCustomer = false,
            BlocksCapacity = true,
            SortOrder = 9,
            IsActive = true,
        });
        await harness.Db.SaveChangesAsync();
        var clinic = await harness.Db.OccupancyKinds.SingleAsync(k => k.Key == "clinic");
        await harness.AddTemplateAsync(clinic.Id, Ten, new TimeOnly(10, 30));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_blocking_reservation_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        harness.SeedBlockingReservation(Tuesday, Ten, Eleven);
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_inactive_tenant_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        harness.Tenant.Deactivate();
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_with_production_scope_rejects_other_tenant_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var service = new TeacherLessonService(
            harness.Db,
            harness.TenantProvider,
            new FakeTrialGuard(),
            new TestTimeProvider(BrazilTimeZone.AtLocal(Tuesday.AddDays(-1), new TimeOnly(9, 0))),
            new FiccTeacherLessonScope());

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Remove_with_production_scope_rejects_unlisted_court_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var unlistedRentalAssetId = await harness.SeedUnlistedFiccCourtWithLessonAsync();
        harness.TenantProvider.TenantId = FiccTeacherLessonScope.TenantId;
        var service = new TeacherLessonService(
            harness.Db,
            harness.TenantProvider,
            new FakeTrialGuard(),
            new TestTimeProvider(BrazilTimeZone.AtLocal(Tuesday.AddDays(-1), new TimeOnly(9, 0))),
            new FiccTeacherLessonScope());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RemoveAsync(
            new RemoveTeacherLessonRequestDto
            {
                RentalAssetId = unlistedRentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
            }, CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Remove_with_test_scope_accepts_the_unlisted_ficc_fixture()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var unlistedRentalAssetId = await harness.SeedUnlistedFiccCourtWithLessonAsync();
        harness.TenantProvider.TenantId = FiccTeacherLessonScope.TenantId;
        var service = new TeacherLessonService(
            harness.Db,
            harness.TenantProvider,
            new FakeTrialGuard(),
            new TestTimeProvider(BrazilTimeZone.AtLocal(Tuesday.AddDays(-1), new TimeOnly(9, 0))),
            new TestTeacherLessonScope());

        var removed = await service.RemoveAsync(new RemoveTeacherLessonRequestDto
        {
            RentalAssetId = unlistedRentalAssetId,
            Date = Tuesday,
            StartTime = Ten,
            EndTime = Eleven,
        }, CancellationToken.None);

        Assert.Equal("open", removed.OccupancyKindKey);
        var slot = Assert.Single(await harness.Db.Slots.ToListAsync());
        Assert.Equal(unlistedRentalAssetId, slot.RentalAssetId);
        Assert.Equal(SlotStatus.Available, slot.Status);
        Assert.Null(slot.ReservationId);
    }

    [Fact]
    public async Task Create_read_only_trial_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync(
            trialGuard: new BlockingTrialGuard());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        Assert.Equal("This trial has ended. The workspace is read-only until it is purged.", ex.Message);
        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_openhours_policy_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var rental = await harness.Db.RentalAssets.SingleAsync();
        rental.SchedulePolicy = SchedulePolicy.OpenHours;
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_inactive_lesson_kind_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var lesson = await harness.Db.OccupancyKinds.SingleAsync(k => k.Key == "lesson");
        lesson.IsActive = false;
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None));

        Assert.Empty(await harness.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Create_does_not_see_other_tenant_asset()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var other = await TeacherLessonHarness.CreateAsync(databaseName: harness.DatabaseName);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.Lessons.CreateAsync(
                new CreateTeacherLessonRequestDto
                {
                    RentalAssetId = other.RentalAssetId,
                    Date = Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                },
                CancellationToken.None));

        other.TenantProvider.TenantId = other.TenantId;
        Assert.Empty(await other.Db.Slots.ToListAsync());
    }

    [Fact]
    public async Task Remove_opens_D_and_leaves_D_plus_7_as_lesson()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var day = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        var nextWeek = await harness.Lessons.CreateAsync(
            harness.CreateRequest(date: NextTuesday),
            CancellationToken.None);

        var opened = await harness.Lessons.RemoveAsync(
            new RemoveTeacherLessonRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
            },
            CancellationToken.None);

        Assert.Equal("open", opened.OccupancyKindKey);
        Assert.Equal(SlotStatus.Available, opened.Status);
        Assert.Equal(harness.OpenTemplateId, opened.SourceTemplateId);

        var remaining = await harness.Db.Slots.SingleAsync(s => s.Id == nextWeek.Id);
        var remainingKind = await harness.Db.OccupancyKinds.SingleAsync(k => k.Id == remaining.OccupancyKindId);
        Assert.Equal("lesson", remainingKind.Key);
        Assert.Equal(1, await harness.Db.ScheduleTemplates.CountAsync());
    }

    [Fact]
    public async Task Remove_derived_weekly_lesson_inserts_open_override_for_D_only()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var kinds = await harness.EnsureKindsAsync();
        await harness.AddTemplateAsync(kinds.Lesson.Id, Ten, Eleven);
        var lessonTemplate = await harness.Db.ScheduleTemplates
            .SingleAsync(template => template.OccupancyKindId == kinds.Lesson.Id);

        var opened = await harness.Lessons.RemoveAsync(
            new RemoveTeacherLessonRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
            },
            CancellationToken.None);

        Assert.Equal("open", opened.OccupancyKindKey);
        Assert.Equal(lessonTemplate.Id, opened.SourceTemplateId);
        Assert.Equal(SlotStatus.Available, opened.Status);
        Assert.Single(await harness.Db.Slots.ToListAsync());

        var nextWeek = await harness.Schedule.GetDayAsync(
            NextTuesday, harness.RentalAssetId, customerFacing: false, CancellationToken.None);
        Assert.Contains(nextWeek.Slots, slot => slot.OccupancyKindKey == "lesson");

        var reservations = harness.CreateReservationService();
        var availability = await reservations.CheckAvailabilityAsync(new CheckAvailabilityRequestDto
        {
            AssetId = harness.AssetId,
            Date = NextTuesday,
            StartTime = Ten,
            EndTime = Eleven,
        }, CancellationToken.None);
        Assert.False(availability.IsAvailable);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reservations.CreateReservationAsync(
                harness.CustomerId,
                new CreateReservationRequestDto
                {
                    UnitId = harness.UnitId,
                    Date = NextTuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                    Items = [new CreateReservationItemRequestDto { AssetId = harness.AssetId, Quantity = 1 }],
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task Remove_already_open_override_is_idempotent()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync(seedHourlyOpen: false);
        var kinds = await harness.EnsureKindsAsync();
        await harness.AddTemplateAsync(kinds.Lesson.Id, Ten, Eleven);
        var request = new RemoveTeacherLessonRequestDto
        {
            RentalAssetId = harness.RentalAssetId,
            Date = Tuesday,
            StartTime = Ten,
            EndTime = Eleven,
        };

        var first = await harness.Lessons.RemoveAsync(request, CancellationToken.None);
        var second = await harness.Lessons.RemoveAsync(request, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("open", second.OccupancyKindKey);
        Assert.Single(await harness.Db.Slots.ToListAsync());
        Assert.Single(await harness.Db.ScheduleTemplates.ToListAsync());
    }

    [Fact]
    public async Task Remove_idempotent_open_override_rejects_when_another_non_open_template_overlaps()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync(seedHourlyOpen: false);
        var kinds = await harness.EnsureKindsAsync();
        await harness.AddTemplateAsync(kinds.Lesson.Id, Ten, Eleven);
        var request = new RemoveTeacherLessonRequestDto
        {
            RentalAssetId = harness.RentalAssetId,
            Date = Tuesday,
            StartTime = Ten,
            EndTime = Eleven,
        };
        await harness.Lessons.RemoveAsync(request, CancellationToken.None);

        var clinic = new OccupancyKind
        {
            TenantId = harness.TenantId,
            Key = "clinic",
            Label = "Clinic",
            IsBookableByCustomer = false,
            BlocksCapacity = true,
            SortOrder = 9,
            IsActive = true,
        };
        harness.Db.OccupancyKinds.Add(clinic);
        await harness.Db.SaveChangesAsync();
        await harness.AddTemplateAsync(clinic.Id, Ten, Eleven);
        var before = Assert.Single(await harness.Db.Slots.ToListAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.RemoveAsync(request, CancellationToken.None));

        var after = Assert.Single(await harness.Db.Slots.ToListAsync());
        Assert.Equal(before.OccupancyKindId, after.OccupancyKindId);
        Assert.Equal(kinds.Open.Id, after.OccupancyKindId);
    }

    [Fact]
    public async Task Remove_then_reservation_on_D_succeeds_and_D_plus_7_stays_blocked()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var day = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        await harness.Lessons.CreateAsync(harness.CreateRequest(date: NextTuesday), CancellationToken.None);
        await harness.Lessons.RemoveAsync(
            new RemoveTeacherLessonRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
            },
            CancellationToken.None);

        var reservations = harness.CreateReservationService();
        var booked = await reservations.CreateReservationAsync(
            harness.CustomerId,
            new CreateReservationRequestDto
            {
                UnitId = harness.UnitId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
                Items = [new CreateReservationItemRequestDto { AssetId = harness.AssetId, Quantity = 1 }],
            },
            CancellationToken.None);

        Assert.Equal(ReservationStatus.PendingDeposit, booked.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reservations.CreateReservationAsync(
                harness.CustomerId,
                new CreateReservationRequestDto
                {
                    UnitId = harness.UnitId,
                    Date = NextTuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                    Items = [new CreateReservationItemRequestDto { AssetId = harness.AssetId, Quantity = 1 }],
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task Template_edit_does_not_change_dated_lesson()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var lesson = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        var kinds = await harness.EnsureKindsAsync();

        await harness.Schedule.UpdateTemplateAsync(
            harness.OpenTemplateId,
            new UpsertScheduleTemplateRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
                OccupancyKindId = kinds.Open.Id,
                Label = "Grade nova",
                IsActive = true,
            },
            CancellationToken.None);

        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == lesson.Id);
        var kind = await harness.Db.OccupancyKinds.SingleAsync(k => k.Id == persisted.OccupancyKindId);
        Assert.Equal("lesson", kind.Key);
        Assert.Null(persisted.Label);
    }

    [Fact]
    public async Task Restore_weekly_default_on_remaining_lesson_returns_to_open_source()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var lesson = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);

        var restored = await harness.Schedule.ApplyDailyOccurrenceAsync(
            new ApplyDailyOccurrenceRequestDto
            {
                SlotId = lesson.Id,
                RentalAssetId = harness.RentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
                Action = DailyOccurrenceAction.RestoreWeeklyDefault,
                Scope = OccurrenceEditScope.OnlyThisDay,
            },
            CancellationToken.None);

        Assert.Equal("open", restored.OccupancyKindKey);
        Assert.Equal(harness.OpenTemplateId, restored.SourceTemplateId);
    }

    [Fact]
    public async Task Remove_reservation_overlap_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var lesson = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        harness.SeedBlockingReservation(Tuesday, Ten, Eleven);
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.RemoveAsync(
                new RemoveTeacherLessonRequestDto
                {
                    RentalAssetId = harness.RentalAssetId,
                    Date = Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                },
                CancellationToken.None));

        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == lesson.Id);
        Assert.Equal((await harness.Db.OccupancyKinds.SingleAsync(k => k.Key == "lesson")).Id, persisted.OccupancyKindId);
    }

    [Fact]
    public async Task Remove_other_tenant_slot_is_not_found()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var lesson = await harness.Lessons.CreateAsync(harness.CreateRequest(), CancellationToken.None);
        var other = await TeacherLessonHarness.CreateAsync(databaseName: harness.DatabaseName);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            other.Lessons.RemoveAsync(
                new RemoveTeacherLessonRequestDto
                {
                    RentalAssetId = harness.RentalAssetId,
                    Date = Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                },
                CancellationToken.None));

        harness.TenantProvider.TenantId = harness.TenantId;
        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == lesson.Id);
        Assert.Equal((await harness.Db.OccupancyKinds.SingleAsync(k => k.Key == "lesson")).Id, persisted.OccupancyKindId);
    }

    [Fact]
    public async Task Remove_non_lesson_slot_rejects_without_write()
    {
        await using var harness = await TeacherLessonHarness.CreateAsync();
        var kinds = await harness.EnsureKindsAsync();
        var openSlot = await harness.Schedule.UpsertSlotAsync(
            new UpsertSlotRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
                OccupancyKindId = kinds.Open.Id,
            },
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Lessons.RemoveAsync(
                new RemoveTeacherLessonRequestDto
                {
                    RentalAssetId = harness.RentalAssetId,
                    Date = Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                },
                CancellationToken.None));

        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == openSlot.Id);
        Assert.Equal(kinds.Open.Id, persisted.OccupancyKindId);
    }
}

internal sealed class BlockingTrialGuard : ITrialGuard
{
    public Task EnsureWritableAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "This trial has ended. The workspace is read-only until it is purged.");

    public Task EnsureCanCreateAssetsAsync(int additionalCount, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnsureCanInviteUserAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class TeacherLessonHarness : IAsyncDisposable
{
    private TeacherLessonHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Tenant tenant,
        Guid unitId,
        Guid customerId,
        Guid assetId,
        Guid rentalAssetId,
        Guid openTemplateId,
        string databaseName,
        ITrialGuard trialGuard)
    {
        Db = db;
        TenantProvider = tenantProvider;
        Tenant = tenant;
        UnitId = unitId;
        CustomerId = customerId;
        AssetId = assetId;
        RentalAssetId = rentalAssetId;
        OpenTemplateId = openTemplateId;
        DatabaseName = databaseName;
        Lessons = new TeacherLessonService(
            db,
            tenantProvider,
            trialGuard,
            new TestTimeProvider(BrazilTimeZone.AtLocal(new DateOnly(2026, 8, 24), new TimeOnly(9, 0))),
            new TestTeacherLessonScope());
        Schedule = new ScheduleService(
            db,
            tenantProvider,
            new OccupancyKindService(db, tenantProvider),
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider),
            SilentRentalsNotifications.Publisher,
            SilentRentalsNotifications.Scheduler);
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Tenant Tenant { get; }

    public Guid TenantId => Tenant.Id;

    public Guid UnitId { get; }

    public Guid CustomerId { get; }

    public Guid AssetId { get; }

    public Guid RentalAssetId { get; }

    public Guid OpenTemplateId { get; }

    public string DatabaseName { get; }

    public TeacherLessonService Lessons { get; }

    public ScheduleService Schedule { get; }

    public static async Task<TeacherLessonHarness> CreateAsync(
        bool seedHourlyOpen = true,
        string? databaseName = null,
        ITrialGuard? trialGuard = null)
    {
        var name = databaseName ?? $"teacher-{Guid.NewGuid():N}";
        var tenantProvider = new FakeTenantProvider();
        var db = InMemoryAppDb.Create(tenantProvider, name, ignoreInMemoryTransactions: true);

        var tenant = new Tenant("Clube Aula", UniqueTaxId(), subdomain: $"aula-{Guid.NewGuid():N}"[..16]);
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
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Cliente B2C",
            Email = $"cliente-{Guid.NewGuid():N}@club.test",
            Phone = "11999999999",
        };
        var pricing = new RentalPricing
        {
            TenantId = tenant.Id,
            RentalAssetId = rental.Id,
            DayOfWeek = DayOfWeek.Tuesday,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(22, 0),
            PricePerHour = 100m,
            RequiresDeposit = true,
            DepositPercentage = 50m,
        };

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.Customers.Add(customer);
        db.RentalPricings.Add(pricing);
        await db.SaveChangesAsync();

        var occupancy = new OccupancyKindService(db, tenantProvider);
        await occupancy.EnsureDefaultsAsync(CancellationToken.None);
        var open = await db.OccupancyKinds.SingleAsync(k => k.Key == "open");

        Guid openTemplateId = Guid.Empty;
        if (seedHourlyOpen)
        {
            var template = new ScheduleTemplate
            {
                TenantId = tenant.Id,
                RentalAssetId = rental.Id,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = new TimeOnly(10, 0),
                EndTime = new TimeOnly(11, 0),
                OccupancyKindId = open.Id,
                IsActive = true,
            };
            db.ScheduleTemplates.Add(template);
            await db.SaveChangesAsync();
            openTemplateId = template.Id;
        }

        return new TeacherLessonHarness(
            db,
            tenantProvider,
            tenant,
            unit.Id,
            customer.Id,
            asset.Id,
            rental.Id,
            openTemplateId,
            name,
            trialGuard ?? new FakeTrialGuard());
    }

    public ReservationService CreateReservationService() =>
        new(
            Db,
            TenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(Db, TenantProvider),
            SilentRentalsNotifications.Publisher,
            SilentRentalsNotifications.Scheduler);

    public async Task<(OccupancyKind Open, OccupancyKind Lesson, OccupancyKind Closed)> EnsureKindsAsync()
    {
        await new OccupancyKindService(Db, TenantProvider).EnsureDefaultsAsync(CancellationToken.None);
        var kinds = await Db.OccupancyKinds.ToListAsync();
        return (
            kinds.Single(k => k.Key == "open"),
            kinds.Single(k => k.Key == "lesson"),
            kinds.Single(k => k.Key == "closed"));
    }

    public async Task AddTemplateAsync(Guid occupancyKindId, TimeOnly start, TimeOnly end)
    {
        Db.ScheduleTemplates.Add(new ScheduleTemplate
        {
            TenantId = TenantId,
            RentalAssetId = RentalAssetId,
            DayOfWeek = DayOfWeek.Tuesday,
            StartTime = start,
            EndTime = end,
            OccupancyKindId = occupancyKindId,
            IsActive = true,
        });
        await Db.SaveChangesAsync();
    }

    public void SeedBlockingReservation(DateOnly date, TimeOnly start, TimeOnly end)
    {
        var reservation = new Reservation
        {
            TenantId = TenantId,
            UnitId = UnitId,
            CustomerId = CustomerId,
            CustomerName = "Existing",
            CustomerWhatsApp = "11999999999",
            StartDateTime = BrazilTimeZone.AtLocal(date, start),
            EndDateTime = BrazilTimeZone.AtLocal(date, end),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 100m,
            DepositPaid = 0m,
        };
        reservation.AddItem(new ReservationItem
        {
            TenantId = TenantId,
            ReservationId = reservation.Id,
            RentalAssetId = RentalAssetId,
            Quantity = 1,
            UnitPrice = 100m,
            SubTotal = 100m,
        });
        Db.Reservations.Add(reservation);
    }

    public async Task<Guid> SeedUnlistedFiccCourtWithLessonAsync()
    {
        var tenant = new Tenant(
            "FICC allowlist test",
            UniqueTaxId(),
            subdomain: $"ficc-test-{Guid.NewGuid():N}"[..18]);
        Db.Entry(tenant).Property(entity => entity.Id).CurrentValue = FiccTeacherLessonScope.TenantId;

        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"ficc-test-{Guid.NewGuid():N}"[..32],
            Label = "Test courts",
            FieldSchemaJson = "{}",
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Unlisted court",
            Tag = "Q-TEST",
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

        Db.Tenants.Add(tenant);
        Db.Units.Add(unit);
        Db.AssetCategories.Add(category);
        Db.AssetFamilies.Add(family);
        Db.Assets.Add(asset);
        Db.RentalAssets.Add(rental);
        await Db.SaveChangesAsync();

        TenantProvider.TenantId = tenant.Id;
        await new OccupancyKindService(Db, TenantProvider).EnsureDefaultsAsync(CancellationToken.None);
        var lesson = await Db.OccupancyKinds.SingleAsync(kind => kind.Key == "lesson");
        Db.ScheduleTemplates.Add(new ScheduleTemplate
        {
            TenantId = tenant.Id,
            RentalAssetId = rental.Id,
            DayOfWeek = DayOfWeek.Tuesday,
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(11, 0),
            OccupancyKindId = lesson.Id,
            IsActive = true,
        });
        await Db.SaveChangesAsync();

        return rental.Id;
    }

    public CreateTeacherLessonRequestDto CreateRequest(
        DateOnly? date = null,
        TimeOnly? start = null,
        TimeOnly? end = null,
        string? label = null) =>
        new()
        {
            RentalAssetId = RentalAssetId,
            Date = date ?? new DateOnly(2026, 8, 25),
            StartTime = start ?? new TimeOnly(10, 0),
            EndTime = end ?? new TimeOnly(11, 0),
            Label = label,
        };

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    private static string UniqueTaxId() =>
        $"{Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999)}";
}
