using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class TeacherLessonConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly DateOnly Tuesday = new(2026, 8, 25);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);

    private readonly PostgresContainerFixture _postgres;

    public TeacherLessonConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Create_lesson_vs_create_reservation_serializes_to_one_safe_outcome()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);

        await using var dbLesson = factory.Create(tenantProvider);
        await using var dbBook = factory.Create(tenantProvider);
        var lessons = CreateLessonService(dbLesson, tenantProvider);
        var reservations = CreateReservationService(dbBook, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(lessons.CreateAsync(CreateLessonRequest(seed), CancellationToken.None)),
            CaptureAsync(reservations.CreateReservationAsync(
                seed.CustomerId,
                CreateReservationRequest(seed),
                CancellationToken.None)));

        var lessonEx = captured[0];
        var bookEx = captured[1];
        Assert.True(
            lessonEx is null ^ bookEx is null,
            $"Expected exactly one winner; lessonEx={lessonEx}, bookEx={bookEx}");

        await using var verify = factory.Create(tenantProvider);
        var blocking = await verify.Reservations.CountAsync(r =>
            r.Status == ReservationStatus.PendingDeposit || r.Status == ReservationStatus.Confirmed);
        var lessonsCount = await verify.Slots.CountAsync(s =>
            s.OccupancyKind.Key == "lesson" && s.Status == SlotStatus.Available);

        if (lessonEx is null)
        {
            Assert.Equal(0, blocking);
            Assert.Equal(1, lessonsCount);
        }
        else
        {
            Assert.Equal(1, blocking);
            Assert.Equal(0, lessonsCount);
            Assert.IsType<InvalidOperationException>(lessonEx);
        }
    }

    [DockerFact]
    public async Task Remove_lesson_vs_create_reservation_does_not_open_unsafely()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);

        await using (var setup = factory.Create(tenantProvider))
        {
            var created = await CreateLessonService(setup, tenantProvider)
                .CreateAsync(CreateLessonRequest(seed), CancellationToken.None);
            seed = seed with { LessonSlotId = created.Id };
        }

        await using var dbRemove = factory.Create(tenantProvider);
        await using var dbBook = factory.Create(tenantProvider);
        var lessons = CreateLessonService(dbRemove, tenantProvider);
        var reservations = CreateReservationService(dbBook, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(lessons.RemoveAsync(
                new RemoveTeacherLessonRequestDto
                {
                    RentalAssetId = seed.RentalAssetId,
                    Date = Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                },
                CancellationToken.None)),
            CaptureAsync(reservations.CreateReservationAsync(
                seed.CustomerId,
                CreateReservationRequest(seed),
                CancellationToken.None)));

        await using var verify = factory.Create(tenantProvider);
        var slot = await verify.Slots
            .Include(s => s.OccupancyKind)
            .SingleAsync(s => s.Id == seed.LessonSlotId);
        var blocking = await verify.Reservations.CountAsync(r =>
            r.Status == ReservationStatus.PendingDeposit || r.Status == ReservationStatus.Confirmed);

        if (captured[0] is null && captured[1] is null)
        {
            Assert.Equal("open", slot.OccupancyKind.Key);
            Assert.Equal(1, blocking);
            return;
        }

        if (captured[0] is null)
        {
            Assert.Equal("open", slot.OccupancyKind.Key);
            Assert.Equal(0, blocking);
            Assert.IsType<InvalidOperationException>(captured[1]);
            return;
        }

        Assert.Equal("lesson", slot.OccupancyKind.Key);
        Assert.Equal(0, blocking);
        Assert.IsType<InvalidOperationException>(captured[0]);
    }

    [DockerFact]
    public async Task Create_lesson_vs_template_write_serializes()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        await using var dbKinds = factory.Create(tenantProvider);
        var closedKindId = await dbKinds.OccupancyKinds
            .Where(kind => kind.TenantId == seed.TenantId && kind.Key == "closed")
            .Select(kind => kind.Id)
            .SingleAsync();

        await using var dbLesson = factory.Create(tenantProvider);
        await using var dbTemplate = factory.Create(tenantProvider);
        var lessons = CreateLessonService(dbLesson, tenantProvider);
        var schedule = CreateScheduleService(dbTemplate, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(lessons.CreateAsync(CreateLessonRequest(seed), CancellationToken.None)),
            CaptureAsync(schedule.UpdateTemplateAsync(
                seed.OpenTemplateId,
                new UpsertScheduleTemplateRequestDto
                {
                    RentalAssetId = seed.RentalAssetId,
                    DayOfWeek = DayOfWeek.Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                    OccupancyKindId = closedKindId,
                    Label = "Closed after teacher write",
                    IsActive = true,
                },
                CancellationToken.None)));

        Assert.Null(captured[1]);

        await using var verify = factory.Create(tenantProvider);
        var template = await verify.ScheduleTemplates.SingleAsync(t => t.Id == seed.OpenTemplateId);
        Assert.True(template.IsActive);
        Assert.Equal(closedKindId, template.OccupancyKindId);
        var lessonSlots = await verify.Slots.CountAsync(s => s.OccupancyKind.Key == "lesson");
        Assert.InRange(lessonSlots, 0, 1);
        if (captured[0] is null)
        {
            var lesson = await verify.Slots.SingleAsync(s => s.OccupancyKind.Key == "lesson");
            Assert.Equal(seed.OpenTemplateId, lesson.SourceTemplateId);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(captured[0]);
            Assert.Equal(0, lessonSlots);
        }
    }

    [DockerFact]
    public async Task Create_lesson_vs_policy_write_serializes()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);

        await using var dbLesson = factory.Create(tenantProvider);
        await using var dbPolicy = factory.Create(tenantProvider);
        var lessons = CreateLessonService(dbLesson, tenantProvider);
        var assets = new RentalAssetService(dbPolicy, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(lessons.CreateAsync(CreateLessonRequest(seed), CancellationToken.None)),
            CaptureAsync(assets.UpdateSchedulePolicyAsync(
                seed.RentalAssetId,
                new UpdateRentalSchedulePolicyRequestDto
                {
                    SchedulePolicy = SchedulePolicy.OpenHours,
                    OpenTime = new TimeOnly(8, 0),
                    CloseTime = new TimeOnly(22, 0),
                    AllowedDurationMinutes = "60",
                },
                CancellationToken.None)));

        await using var verify = factory.Create(tenantProvider);
        var rental = await verify.RentalAssets.SingleAsync(r => r.Id == seed.RentalAssetId);
        Assert.Equal(SchedulePolicy.OpenHours, rental.SchedulePolicy);
        var lessonSlots = await verify.Slots.CountAsync(s => s.OccupancyKind.Key == "lesson");

        if (captured[0] is null)
        {
            Assert.Equal(1, lessonSlots);
            var lesson = await verify.Slots.SingleAsync(s => s.OccupancyKind.Key == "lesson");
            Assert.Equal(seed.OpenTemplateId, lesson.SourceTemplateId);
        }
        else
        {
            Assert.Equal(0, lessonSlots);
            Assert.IsType<InvalidOperationException>(captured[0]);
        }
    }

    [DockerFact]
    public async Task Teacher_create_waits_for_same_tenant_rental_asset_lock()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);

        await AssertOperationWaitsForAssetLockAsync(factory, tenantProvider, seed, async db =>
        {
            var lessons = CreateLessonService(db, tenantProvider);
            await lessons.CreateAsync(CreateLessonRequest(seed), CancellationToken.None);
        }, async () =>
        {
            await using var verify = factory.Create(tenantProvider);
            Assert.Equal(1, await verify.Slots.CountAsync(slot =>
                slot.RentalAssetId == seed.RentalAssetId && slot.OccupancyKind.Key == "lesson"));
        });
    }

    [DockerFact]
    public async Task Teacher_remove_waits_for_same_tenant_rental_asset_lock()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        await using (var setup = factory.Create(tenantProvider))
        {
            var created = await CreateLessonService(setup, tenantProvider)
                .CreateAsync(CreateLessonRequest(seed), CancellationToken.None);
            seed = seed with { LessonSlotId = created.Id };
        }

        await AssertOperationWaitsForAssetLockAsync(factory, tenantProvider, seed, async db =>
        {
            var lessons = CreateLessonService(db, tenantProvider);
            await lessons.RemoveAsync(new RemoveTeacherLessonRequestDto
            {
                RentalAssetId = seed.RentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
            }, CancellationToken.None);
        }, async () =>
        {
            await using var verify = factory.Create(tenantProvider);
            var slot = await verify.Slots.Include(candidate => candidate.OccupancyKind)
                .SingleAsync(candidate => candidate.Id == seed.LessonSlotId);
            Assert.Equal("open", slot.OccupancyKind.Key);
        });
    }

    [DockerFact]
    public async Task Admin_schedule_policy_write_waits_for_same_tenant_rental_asset_lock()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);

        await AssertOperationWaitsForAssetLockAsync(factory, tenantProvider, seed, async db =>
        {
            var assets = new RentalAssetService(db, tenantProvider);
            await assets.UpdateSchedulePolicyAsync(seed.RentalAssetId,
                new UpdateRentalSchedulePolicyRequestDto
                {
                    SchedulePolicy = SchedulePolicy.OpenHours,
                    OpenTime = new TimeOnly(8, 0),
                    CloseTime = new TimeOnly(22, 0),
                    AllowedDurationMinutes = "60",
                }, CancellationToken.None);
        }, async () =>
        {
            await using var verify = factory.Create(tenantProvider);
            Assert.Equal(SchedulePolicy.OpenHours,
                (await verify.RentalAssets.SingleAsync(rental => rental.Id == seed.RentalAssetId)).SchedulePolicy);
        });
    }

    [DockerFact]
    public async Task Admin_template_write_waits_for_same_tenant_rental_asset_lock()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        await using var kindsDb = factory.Create(tenantProvider);
        var closedKindId = await kindsDb.OccupancyKinds
            .Where(kind => kind.TenantId == seed.TenantId && kind.Key == "closed")
            .Select(kind => kind.Id)
            .SingleAsync();

        await AssertOperationWaitsForAssetLockAsync(factory, tenantProvider, seed, async db =>
        {
            var schedule = CreateScheduleService(db, tenantProvider);
            await schedule.UpdateTemplateAsync(seed.OpenTemplateId,
                new UpsertScheduleTemplateRequestDto
                {
                    RentalAssetId = seed.RentalAssetId,
                    DayOfWeek = DayOfWeek.Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                    OccupancyKindId = closedKindId,
                    Label = "Closed after teacher write",
                    IsActive = true,
                }, CancellationToken.None);
        }, async () =>
        {
            await using var verify = factory.Create(tenantProvider);
            Assert.Equal(closedKindId,
                (await verify.ScheduleTemplates.SingleAsync(template => template.Id == seed.OpenTemplateId)).OccupancyKindId);
        });
    }

    [DockerFact]
    public async Task Wrong_tenant_asset_lock_does_not_block_owner_schedule_policy_write()
    {
        var factory = RequireFactory();
        var ownerProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, ownerProvider);
        var wrongTenantProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };

        await using var wrongTenantDb = factory.Create(wrongTenantProvider);
        await using var wrongTenantTransaction = await wrongTenantDb.Database.BeginTransactionAsync();
        await RentalAssetLocks.LockByRentalAssetIdAsync(
            wrongTenantDb, wrongTenantProvider.TenantId!.Value, seed.RentalAssetId, CancellationToken.None);

        await using var ownerDb = factory.Create(ownerProvider);
        var ownerWrite = new RentalAssetService(ownerDb, ownerProvider).UpdateSchedulePolicyAsync(
            seed.RentalAssetId,
            new UpdateRentalSchedulePolicyRequestDto
            {
                SchedulePolicy = SchedulePolicy.OpenHours,
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(22, 0),
                AllowedDurationMinutes = "60",
            }, CancellationToken.None);

        try
        {
            var completed = await Task.WhenAny(ownerWrite, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(ownerWrite, completed);
            await ownerWrite;
            await using var verify = factory.Create(ownerProvider);
            Assert.Equal(SchedulePolicy.OpenHours,
                (await verify.RentalAssets.SingleAsync(rental => rental.Id == seed.RentalAssetId)).SchedulePolicy);
        }
        finally
        {
            await wrongTenantTransaction.RollbackAsync();
        }
    }

    [DockerFact]
    public async Task Concurrent_duplicate_lesson_creates_are_idempotent_and_leave_one_slot()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);

        await using var dbFirst = factory.Create(tenantProvider);
        await using var dbSecond = factory.Create(tenantProvider);
        var captured = await Task.WhenAll(
            CaptureAsync(CreateLessonService(dbFirst, tenantProvider)
                .CreateAsync(CreateLessonRequest(seed), CancellationToken.None)),
            CaptureAsync(CreateLessonService(dbSecond, tenantProvider)
                .CreateAsync(CreateLessonRequest(seed), CancellationToken.None)));

        Assert.All(captured, exception => Assert.Null(exception));
        await using var verify = factory.Create(tenantProvider);
        var lessons = await verify.Slots
            .Where(slot => slot.OccupancyKind.Key == "lesson")
            .ToListAsync();
        Assert.Single(lessons);
    }

    [DockerFact]
    public async Task Create_lesson_vs_book_slot_serializes_to_one_safe_outcome()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        Guid slotId;
        await using (var setup = factory.Create(tenantProvider))
        {
            var schedule = CreateScheduleService(setup, tenantProvider);
            await schedule.PublishDayAsync(
                new PublishDayRequestDto
                {
                    RentalAssetId = seed.RentalAssetId,
                    Date = Tuesday,
                },
                CancellationToken.None);
            var slot = await setup.Slots.SingleAsync(candidate =>
                candidate.TenantId == seed.TenantId
                && candidate.RentalAssetId == seed.RentalAssetId
                && candidate.Date == Tuesday
                && candidate.StartTime == Ten);
            slotId = slot.Id;
        }

        await using var dbLesson = factory.Create(tenantProvider);
        await using var dbBook = factory.Create(tenantProvider);
        var captured = await Task.WhenAll(
            CaptureAsync(CreateLessonService(dbLesson, tenantProvider)
                .CreateAsync(CreateLessonRequest(seed), CancellationToken.None)),
            CaptureAsync(CreateScheduleService(dbBook, tenantProvider).BookSlotAsync(
                seed.CustomerId,
                new BookSlotRequestDto
                {
                    SlotId = slotId,
                    UnitId = seed.UnitId,
                    Quantity = 1,
                },
                CancellationToken.None)));

        Assert.True(captured[0] is null ^ captured[1] is null,
            $"Expected one winner; lessonEx={captured[0]}, bookingEx={captured[1]}");
        await using var verify = factory.Create(tenantProvider);
        var slotState = await verify.Slots
            .Include(slot => slot.OccupancyKind)
            .SingleAsync(slot => slot.Id == slotId);
        var blockingReservations = await verify.Reservations.CountAsync(reservation =>
            reservation.Status == ReservationStatus.PendingDeposit
            || reservation.Status == ReservationStatus.Confirmed);
        if (captured[0] is null)
        {
            Assert.Equal("lesson", slotState.OccupancyKind.Key);
            Assert.Equal(0, blockingReservations);
        }
        else
        {
            Assert.Equal(SlotStatus.Booked, slotState.Status);
            Assert.Equal(1, blockingReservations);
            Assert.IsType<InvalidOperationException>(captured[0]);
        }
    }

    [DockerFact]
    public async Task Create_lesson_vs_publish_day_serializes_and_keeps_one_lesson_slot()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        await using var dbLesson = factory.Create(tenantProvider);
        await using var dbPublish = factory.Create(tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(CreateLessonService(dbLesson, tenantProvider)
                .CreateAsync(CreateLessonRequest(seed), CancellationToken.None)),
            CaptureAsync(CreateScheduleService(dbPublish, tenantProvider).PublishDayAsync(
                new PublishDayRequestDto
                {
                    RentalAssetId = seed.RentalAssetId,
                    Date = Tuesday,
                },
                CancellationToken.None)));

        Assert.All(captured, exception => Assert.Null(exception));
        await using var verify = factory.Create(tenantProvider);
        var slots = await verify.Slots
            .Include(slot => slot.OccupancyKind)
            .Where(slot => slot.TenantId == seed.TenantId
                           && slot.RentalAssetId == seed.RentalAssetId
                           && slot.Date == Tuesday
                           && slot.StartTime == Ten)
            .ToListAsync();
        var slot = Assert.Single(slots);
        Assert.Equal("lesson", slot.OccupancyKind.Key);
        Assert.Equal(seed.OpenTemplateId, slot.SourceTemplateId);
    }

    [DockerFact]
    public async Task Create_lesson_vs_daily_occurrence_update_keeps_admin_close_when_both_succeed()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        Guid closedKindId;
        Guid slotId;
        await using (var setup = factory.Create(tenantProvider))
        {
            var kinds = await setup.OccupancyKinds
                .Where(kind => kind.TenantId == seed.TenantId)
                .ToListAsync();
            closedKindId = kinds.Single(kind => kind.Key == "closed").Id;
            var schedule = CreateScheduleService(setup, tenantProvider);
            await schedule.PublishDayAsync(
                new PublishDayRequestDto { RentalAssetId = seed.RentalAssetId, Date = Tuesday },
                CancellationToken.None);
            slotId = await setup.Slots
                .Where(slot => slot.TenantId == seed.TenantId
                               && slot.RentalAssetId == seed.RentalAssetId
                               && slot.Date == Tuesday
                               && slot.StartTime == Ten)
                .Select(slot => slot.Id)
                .SingleAsync();
        }

        await using var dbLesson = factory.Create(tenantProvider);
        await using var dbAdmin = factory.Create(tenantProvider);
        var captured = await Task.WhenAll(
            CaptureAsync(CreateLessonService(dbLesson, tenantProvider)
                .CreateAsync(CreateLessonRequest(seed), CancellationToken.None)),
            CaptureAsync(CreateScheduleService(dbAdmin, tenantProvider).ApplyDailyOccurrenceAsync(
                new ApplyDailyOccurrenceRequestDto
                {
                    SlotId = slotId,
                    RentalAssetId = seed.RentalAssetId,
                    Date = Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                    Action = DailyOccurrenceAction.Update,
                    Scope = OccurrenceEditScope.OnlyThisDay,
                    OccupancyKindId = closedKindId,
                },
                CancellationToken.None)));

        Assert.Null(captured[1]);
        await using var verify = factory.Create(tenantProvider);
        var finalSlot = await verify.Slots
            .Include(slot => slot.OccupancyKind)
            .SingleAsync(slot => slot.Id == slotId);
        Assert.Equal("closed", finalSlot.OccupancyKind.Key);
        Assert.Equal(SlotStatus.Available, finalSlot.Status);
        if (captured[0] is not null)
        {
            Assert.IsType<InvalidOperationException>(captured[0]);
        }
    }

    [DockerFact]
    public async Task Cross_tenant_asset_id_is_rejected_without_changing_or_blocking_other_tenant()
    {
        var factory = RequireFactory();
        var teacherTenant = new FakeTenantProvider();
        var teacherGrid = await SeedSlotGridAsync(factory, teacherTenant);
        var ownerTenant = new FakeTenantProvider();
        var ownerGrid = await SeedSlotGridAsync(factory, ownerTenant);

        await using var attemptedWrite = factory.Create(teacherTenant);
        var exception = await CaptureAsync(CreateLessonService(attemptedWrite, teacherTenant)
            .CreateAsync(new CreateTeacherLessonRequestDto
            {
                RentalAssetId = ownerGrid.RentalAssetId,
                Date = Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
            }, CancellationToken.None));
        Assert.IsType<KeyNotFoundException>(exception);

        await using var ownerWrite = factory.Create(ownerTenant);
        var schedule = CreateScheduleService(ownerWrite, ownerTenant);
        await schedule.UpdateTemplateAsync(
            ownerGrid.OpenTemplateId,
            new UpsertScheduleTemplateRequestDto
            {
                RentalAssetId = ownerGrid.RentalAssetId,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
                OccupancyKindId = ownerGrid.OpenKindId,
                Label = "Owner tenant still writable",
                IsActive = true,
            },
            CancellationToken.None);

        await using var verifyTeacher = factory.Create(teacherTenant);
        Assert.Empty(await verifyTeacher.Slots
            .Where(slot => slot.RentalAssetId == ownerGrid.RentalAssetId)
            .ToListAsync());
        await using var verifyOwner = factory.Create(ownerTenant);
        var ownerTemplate = await verifyOwner.ScheduleTemplates
            .SingleAsync(template => template.Id == ownerGrid.OpenTemplateId);
        Assert.Equal("Owner tenant still writable", ownerTemplate.Label);
        Assert.Empty(await verifyOwner.Slots
            .Where(slot => slot.RentalAssetId == ownerGrid.RentalAssetId)
            .ToListAsync());
    }

    [DockerFact]
    public async Task Reservation_rejects_an_interval_split_by_a_closed_winner()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        await using (var setup = factory.Create(tenantProvider))
        {
            var closedKindId = await setup.OccupancyKinds
                .Where(kind => kind.TenantId == seed.TenantId && kind.Key == "closed")
                .Select(kind => kind.Id)
                .SingleAsync();
            setup.ScheduleTemplates.Add(new ScheduleTemplate
            {
                TenantId = seed.TenantId,
                RentalAssetId = seed.RentalAssetId,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = Ten,
                EndTime = new TimeOnly(10, 30),
                OccupancyKindId = closedKindId,
                IsActive = true,
            });
            await setup.SaveChangesAsync();
        }

        await using var reservationDb = factory.Create(tenantProvider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateReservationService(
                reservationDb, tenantProvider)
            .CreateReservationAsync(
                seed.CustomerId,
                CreateReservationRequest(seed),
                CancellationToken.None));

        await using var verify = factory.Create(tenantProvider);
        Assert.Empty(await verify.Reservations.ToListAsync());
    }

    [DockerFact]
    public async Task Reservation_accepts_the_unique_open_window_after_precedence_merge()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        await using (var setup = factory.Create(tenantProvider))
        {
            setup.OccupancyKinds.Add(new OccupancyKind
            {
                TenantId = seed.TenantId,
                Key = "aaa-open-shadow",
                Label = "Lower precedence open",
                IsBookableByCustomer = true,
                BlocksCapacity = false,
                SortOrder = 50,
                IsActive = true,
            });
            await setup.SaveChangesAsync();
            var shadowKindId = await setup.OccupancyKinds
                .Where(kind => kind.TenantId == seed.TenantId && kind.Key == "aaa-open-shadow")
                .Select(kind => kind.Id)
                .SingleAsync();
            setup.ScheduleTemplates.Add(new ScheduleTemplate
            {
                TenantId = seed.TenantId,
                RentalAssetId = seed.RentalAssetId,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = new TimeOnly(10, 30),
                EndTime = new TimeOnly(11, 30),
                OccupancyKindId = shadowKindId,
                IsActive = true,
            });
            await setup.SaveChangesAsync();
        }

        await using var reservationDb = factory.Create(tenantProvider);
        var created = await CreateReservationService(reservationDb, tenantProvider)
            .CreateReservationAsync(
                seed.CustomerId,
                CreateReservationRequest(seed),
                CancellationToken.None);

        Assert.NotEqual(Guid.Empty, created.Id);
        await using var verify = factory.Create(tenantProvider);
        Assert.Single(await verify.Reservations.ToListAsync());
    }

    [DockerFact]
    public async Task Remove_derived_lesson_vs_template_write_serializes()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        Guid lessonTemplateId;
        Guid closedKindId;
        await using (var setup = factory.Create(tenantProvider))
        {
            await new OccupancyKindService(setup, tenantProvider).EnsureDefaultsAsync(CancellationToken.None);
            var kinds = await setup.OccupancyKinds.ToListAsync();
            var lessonKind = kinds.Single(k => k.Key == "lesson");
            closedKindId = kinds.Single(k => k.Key == "closed").Id;
            var template = new ScheduleTemplate
            {
                TenantId = seed.TenantId,
                RentalAssetId = seed.RentalAssetId,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
                OccupancyKindId = lessonKind.Id,
                IsActive = true,
            };
            setup.ScheduleTemplates.Add(template);
            await setup.SaveChangesAsync();
            lessonTemplateId = template.Id;
        }

        await using var dbRemove = factory.Create(tenantProvider);
        await using var dbTemplate = factory.Create(tenantProvider);
        var lessons = CreateLessonService(dbRemove, tenantProvider);
        var schedule = CreateScheduleService(dbTemplate, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(lessons.RemoveAsync(
                new RemoveTeacherLessonRequestDto
                {
                    RentalAssetId = seed.RentalAssetId,
                    Date = Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                },
                CancellationToken.None)),
            CaptureAsync(schedule.UpdateTemplateAsync(
                lessonTemplateId,
                new UpsertScheduleTemplateRequestDto
                {
                    RentalAssetId = seed.RentalAssetId,
                    DayOfWeek = DayOfWeek.Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                    OccupancyKindId = closedKindId,
                    IsActive = true,
                },
                CancellationToken.None)));

        await using var verify = factory.Create(tenantProvider);
        var templateKindId = await verify.ScheduleTemplates
            .Where(t => t.Id == lessonTemplateId)
            .Select(t => t.OccupancyKindId)
            .SingleAsync();
        Assert.Equal(closedKindId, templateKindId);
        var slots = await verify.Slots.Include(s => s.OccupancyKind).ToListAsync();
        if (captured[0] is null)
        {
            Assert.Equal(lessonTemplateId, slots.Single().SourceTemplateId);
            Assert.Equal("open", slots.Single().OccupancyKind.Key);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(captured[0]);
            Assert.Empty(slots);
        }
    }

    [DockerFact]
    public async Task Remove_derived_lesson_vs_policy_write_serializes()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedSlotGridAsync(factory, tenantProvider);
        await using (var setup = factory.Create(tenantProvider))
        {
            await new OccupancyKindService(setup, tenantProvider).EnsureDefaultsAsync(CancellationToken.None);
            var lessonKind = await setup.OccupancyKinds.SingleAsync(k => k.Key == "lesson");
            setup.ScheduleTemplates.Add(new ScheduleTemplate
            {
                TenantId = seed.TenantId,
                RentalAssetId = seed.RentalAssetId,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
                OccupancyKindId = lessonKind.Id,
                IsActive = true,
            });
            await setup.SaveChangesAsync();
        }

        await using var dbRemove = factory.Create(tenantProvider);
        await using var dbPolicy = factory.Create(tenantProvider);
        var lessons = CreateLessonService(dbRemove, tenantProvider);
        var assets = new RentalAssetService(dbPolicy, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(lessons.RemoveAsync(
                new RemoveTeacherLessonRequestDto
                {
                    RentalAssetId = seed.RentalAssetId,
                    Date = Tuesday,
                    StartTime = Ten,
                    EndTime = Eleven,
                },
                CancellationToken.None)),
            CaptureAsync(assets.UpdateSchedulePolicyAsync(
                seed.RentalAssetId,
                new UpdateRentalSchedulePolicyRequestDto
                {
                    SchedulePolicy = SchedulePolicy.OpenHours,
                    OpenTime = new TimeOnly(8, 0),
                    CloseTime = new TimeOnly(22, 0),
                    AllowedDurationMinutes = "60",
                },
                CancellationToken.None)));

        await using var verify = factory.Create(tenantProvider);
        var rental = await verify.RentalAssets.SingleAsync(r => r.Id == seed.RentalAssetId);
        Assert.Equal(SchedulePolicy.OpenHours, rental.SchedulePolicy);
        var slots = await verify.Slots.Include(s => s.OccupancyKind).ToListAsync();
        if (captured[0] is null)
        {
            Assert.Equal("open", slots.Single().OccupancyKind.Key);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(captured[0]);
            Assert.Empty(slots);
        }
    }

    private static async Task AssertOperationWaitsForAssetLockAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        SeededGrid seed,
        Func<AppDbContext, Task> operation,
        Func<Task> verify)
    {
        await using var lockDb = factory.Create(tenantProvider);
        await using var heldLock = await lockDb.Database.BeginTransactionAsync();
        await lockDb.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1
            FROM rentals.rental_assets
            WHERE id = {seed.RentalAssetId}
              AND tenant_id = {seed.TenantId}
            FOR NO KEY UPDATE
            """);

        await using var operationDb = factory.Create(tenantProvider);
        await operationDb.Database.OpenConnectionAsync();
        var connection = operationDb.Database.GetDbConnection();
        await using var pidCommand = connection.CreateCommand();
        pidCommand.CommandText = "SELECT pg_backend_pid()";
        var backendPid = Convert.ToInt32(await pidCommand.ExecuteScalarAsync());

        var pendingOperation = operation(operationDb);
        var lockReleased = false;
        try
        {
            var blocked = await WaitForPostgresLockWaitAsync(factory, tenantProvider, backendPid);
            Assert.True(blocked, "The operation's rental asset FOR UPDATE query did not wait on the held row lock.");
            Assert.False(pendingOperation.IsCompleted, "The operation completed before the asset lock was released.");

            await heldLock.CommitAsync();
            lockReleased = true;
            await pendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
            await verify();
        }
        finally
        {
            if (!lockReleased)
            {
                await heldLock.RollbackAsync();
                try
                {
                    await pendingOperation.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Preserve the assertion or timeout that caused cleanup.
                }
            }
        }
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

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
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
        return _postgres.Factory;
    }

    private static async Task<SeededGrid> SeedSlotGridAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider)
    {
        await using var db = factory.Create(tenantProvider);

        var tenant = new Tenant("Clube Lesson Lock", UniqueTaxId(), subdomain: $"llock-{Guid.NewGuid():N}"[..20]);
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
            Email = "cliente@club.test",
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

        await new OccupancyKindService(db, tenantProvider).EnsureDefaultsAsync(CancellationToken.None);
        var open = await db.OccupancyKinds.SingleAsync(k => k.Key == "open");
        var template = new ScheduleTemplate
        {
            TenantId = tenant.Id,
            RentalAssetId = rental.Id,
            DayOfWeek = DayOfWeek.Tuesday,
            StartTime = Ten,
            EndTime = Eleven,
            OccupancyKindId = open.Id,
            IsActive = true,
        };
        db.ScheduleTemplates.Add(template);
        await db.SaveChangesAsync();

        return new SeededGrid(
            tenant.Id,
            unit.Id,
            customer.Id,
            asset.Id,
            rental.Id,
            template.Id,
            open.Id,
            LessonSlotId: null);
    }

    private static TeacherLessonService CreateLessonService(
        AppDbContext db,
        FakeTenantProvider tenantProvider) =>
        new(db, tenantProvider, new FakeTrialGuard(),
            new TestTimeProvider(BrazilTimeZone.AtLocal(Tuesday.AddDays(-1), new TimeOnly(9, 0))),
            new TestTeacherLessonScope());

    private static ReservationService CreateReservationService(
        AppDbContext db,
        FakeTenantProvider tenantProvider) =>
        new(
            db,
            tenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider),
            SilentRentalsNotifications.Publisher,
            SilentRentalsNotifications.Scheduler);

    private static ScheduleService CreateScheduleService(
        AppDbContext db,
        FakeTenantProvider tenantProvider) =>
        new(
            db,
            tenantProvider,
            new OccupancyKindService(db, tenantProvider),
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider),
            SilentRentalsNotifications.Publisher,
            SilentRentalsNotifications.Scheduler);

    private static CreateTeacherLessonRequestDto CreateLessonRequest(SeededGrid seed) =>
        new()
        {
            RentalAssetId = seed.RentalAssetId,
            Date = Tuesday,
            StartTime = Ten,
            EndTime = Eleven,
        };

    private static CreateReservationRequestDto CreateReservationRequest(SeededGrid seed) =>
        new()
        {
            UnitId = seed.UnitId,
            Date = Tuesday,
            StartTime = Ten,
            EndTime = Eleven,
            Items = [new CreateReservationItemRequestDto { AssetId = seed.AssetId, Quantity = 1 }],
        };

    private static async Task<Exception?> CaptureAsync(Task task)
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

    private static string UniqueTaxId() => Guid.NewGuid().ToString("N")[..14];

    private sealed record SeededGrid(
        Guid TenantId,
        Guid UnitId,
        Guid CustomerId,
        Guid AssetId,
        Guid RentalAssetId,
        Guid OpenTemplateId,
        Guid OpenKindId,
        Guid? LessonSlotId);
}
