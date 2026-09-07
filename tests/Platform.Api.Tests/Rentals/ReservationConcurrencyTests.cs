using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;
using Xunit.Abstractions;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private const int CompleteCancelRaceIterations = 8;
    private const int ConfirmCancelRaceIterations = 16;

    private static readonly DateOnly Date = new(2026, 9, 1);
    private static readonly DateOnly FutureOccupancyDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(21));
    private static readonly DateOnly PastOccupancyDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-3));
    private static readonly TimeOnly Start = new(10, 0);
    private static readonly TimeOnly End = new(11, 0);

    private readonly PostgresContainerFixture _postgres;
    private readonly ITestOutputHelper _output;

    public ReservationConcurrencyTests(PostgresContainerFixture postgres, ITestOutputHelper output)
    {
        _postgres = postgres;
        _output = output;
    }

    [DockerFact]
    public async Task CreateReservationAsync_parallel_same_location_allows_only_one_blocking()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedLocationAsync(factory, tenantProvider, includeSlot: false);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var service1 = new ReservationService(
            db1,
            tenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(db1, tenantProvider));
        var service2 = new ReservationService(
            db2,
            tenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(db2, tenantProvider));

        var request = new CreateReservationRequestDto
        {
            UnitId = seed.UnitId,
            Date = Date,
            StartTime = Start,
            EndTime = End,
            Items =
            [
                new CreateReservationItemRequestDto { AssetId = seed.AssetId, Quantity = 1 }
            ]
        };

        var captured = await Task.WhenAll(
            CaptureAsync(service1.CreateReservationAsync(seed.CustomerId, request, CancellationToken.None)),
            CaptureAsync(service2.CreateReservationAsync(seed.CustomerId, request, CancellationToken.None)));

        AssertSingleWinner(captured);

        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await CountBlockingAsync(verify));
    }

    [DockerFact]
    public async Task BookSlotAsync_parallel_same_location_allows_only_one_blocking()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedLocationAsync(factory, tenantProvider, includeSlot: true);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var occupancyKinds = new UnusedOccupancyKindService();
        var service1 = new ScheduleService(
            db1,
            tenantProvider,
            occupancyKinds,
            new FakeTrialGuard(),
            TestReservationQueue.Create(db1, tenantProvider));
        var service2 = new ScheduleService(
            db2,
            tenantProvider,
            occupancyKinds,
            new FakeTrialGuard(),
            TestReservationQueue.Create(db2, tenantProvider));

        var request = new BookSlotRequestDto
        {
            SlotId = seed.SlotId!.Value,
            UnitId = seed.UnitId,
            Quantity = 1
        };

        var captured = await Task.WhenAll(
            CaptureAsync(service1.BookSlotAsync(seed.CustomerId, request, CancellationToken.None)),
            CaptureAsync(service2.BookSlotAsync(seed.CustomerId, request, CancellationToken.None)));

        AssertSingleWinner(captured);

        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await CountBlockingAsync(verify));
    }

    [DockerFact]
    public async Task CancelAsync_then_create_succeeds_on_same_interval()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(factory, tenantProvider, includeSlot: false);

        await using var db = factory.Create(tenantProvider);
        var service = CreateReservationService(db, tenantProvider);
        await service.CancelAsync(occupied.ReservationId, CancellationToken.None);

        var created = await service.CreateReservationAsync(
            occupied.Seed.CustomerId,
            CreateRequest(occupied.Seed),
            CancellationToken.None);

        Assert.NotEqual(occupied.ReservationId, created.Id);
        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await CountBlockingAsync(verify));
        Assert.Equal(
            ReservationStatus.Canceled,
            await verify.Reservations
                .Where(r => r.Id == occupied.ReservationId)
                .Select(r => r.Status)
                .SingleAsync());
    }

    [DockerFact]
    public async Task CancelAsync_then_book_succeeds_on_released_slot()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(factory, tenantProvider, includeSlot: true);

        await using var db = factory.Create(tenantProvider);
        var reservationService = CreateReservationService(db, tenantProvider);
        await reservationService.CancelAsync(occupied.ReservationId, CancellationToken.None);

        var scheduleService = new ScheduleService(
            db,
            tenantProvider,
            new UnusedOccupancyKindService(),
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider));
        var booked = await scheduleService.BookSlotAsync(
            occupied.Seed.CustomerId,
            new BookSlotRequestDto
            {
                SlotId = occupied.Seed.SlotId!.Value,
                UnitId = occupied.Seed.UnitId,
                Quantity = 1,
            },
            CancellationToken.None);

        Assert.NotEqual(occupied.ReservationId, booked.Id);
        await using var verify = factory.Create(tenantProvider);
        var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
        Assert.Equal(SlotStatus.Booked, slot.Status);
        Assert.Equal(booked.Id, slot.ReservationId);
    }

    [DockerFact]
    public async Task CancelAsync_vs_create_leaves_consistent_occupancy()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(factory, tenantProvider, includeSlot: false);

        await using var dbCancel = factory.Create(tenantProvider);
        await using var dbCreate = factory.Create(tenantProvider);
        var cancelService = CreateReservationService(dbCancel, tenantProvider);
        var createService = CreateReservationService(dbCreate, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None)),
            CaptureAsync(createService.CreateReservationAsync(
                occupied.Seed.CustomerId,
                CreateRequest(occupied.Seed),
                CancellationToken.None)));

        Assert.Null(captured[0]);
        await using var verify = factory.Create(tenantProvider);
        var original = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
        Assert.Equal(ReservationStatus.Canceled, original.Status);

        var blocking = await CountBlockingAsync(verify);
        Assert.InRange(blocking, 0, 1);
        if (captured[1] is null)
        {
            Assert.Equal(1, blocking);
        }
        else
        {
            Assert.Equal(0, blocking);
            Assert.IsType<InvalidOperationException>(captured[1]);
        }
    }

    [DockerFact]
    public async Task CancelAsync_vs_book_leaves_consistent_occupancy()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(factory, tenantProvider, includeSlot: true);

        await using var dbCancel = factory.Create(tenantProvider);
        await using var dbBook = factory.Create(tenantProvider);
        var cancelService = CreateReservationService(dbCancel, tenantProvider);
        var bookService = new ScheduleService(
            dbBook,
            tenantProvider,
            new UnusedOccupancyKindService(),
            new FakeTrialGuard(),
            TestReservationQueue.Create(dbBook, tenantProvider));

        var captured = await Task.WhenAll(
            CaptureAsync(cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None)),
            CaptureAsync(bookService.BookSlotAsync(
                occupied.Seed.CustomerId,
                new BookSlotRequestDto
                {
                    SlotId = occupied.Seed.SlotId!.Value,
                    UnitId = occupied.Seed.UnitId,
                    Quantity = 1,
                },
                CancellationToken.None)));

        Assert.Null(captured[0]);
        await using var verify = factory.Create(tenantProvider);
        var original = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
        Assert.Equal(ReservationStatus.Canceled, original.Status);

        var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
        var blocking = await CountBlockingAsync(verify);
        Assert.InRange(blocking, 0, 1);

        if (captured[1] is null)
        {
            Assert.Equal(1, blocking);
            Assert.Equal(SlotStatus.Booked, slot.Status);
            Assert.NotEqual(occupied.ReservationId, slot.ReservationId);
        }
        else
        {
            Assert.Equal(0, blocking);
            Assert.Equal(SlotStatus.Available, slot.Status);
            Assert.Null(slot.ReservationId);
            Assert.IsType<InvalidOperationException>(captured[1]);
        }
    }

    [DockerFact]
    public async Task CancelAsync_multi_item_then_create_on_both_locations_succeeds()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedTwoLocationsAsync(factory, tenantProvider);
        var reservationId = await SeedConfirmedMultiItemAsync(factory, tenantProvider, seed);

        await using var db = factory.Create(tenantProvider);
        var service = CreateReservationService(db, tenantProvider);
        await service.CancelAsync(reservationId, CancellationToken.None);

        var created = await service.CreateReservationAsync(
            seed.CustomerId,
            CreateMultiItemRequest(seed),
            CancellationToken.None);

        Assert.Equal(2, created.Items.Count);
        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await CountBlockingAsync(verify));
        Assert.Equal(
            ReservationStatus.Canceled,
            await verify.Reservations.Where(r => r.Id == reservationId).Select(r => r.Status).SingleAsync());
    }

    [DockerFact]
    public async Task CancelAsync_vs_create_two_locations_lock_order_does_not_deadlock()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedTwoLocationsAsync(factory, tenantProvider);
        var reservationId = await SeedConfirmedMultiItemAsync(factory, tenantProvider, seed);

        await using var dbCancel = factory.Create(tenantProvider);
        await using var dbCreate = factory.Create(tenantProvider);
        var cancelService = CreateReservationService(dbCancel, tenantProvider);
        var createService = CreateReservationService(dbCreate, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(cancelService.CancelAsync(reservationId, CancellationToken.None)),
            CaptureAsync(createService.CreateReservationAsync(
                seed.CustomerId,
                CreateMultiItemRequest(seed),
                CancellationToken.None)));

        Assert.Null(captured[0]);
        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(
            ReservationStatus.Canceled,
            await verify.Reservations.Where(r => r.Id == reservationId).Select(r => r.Status).SingleAsync());
        Assert.InRange(await CountBlockingAsync(verify), 0, 1);
        if (captured[1] is not null)
        {
            Assert.IsType<InvalidOperationException>(captured[1]);
        }
    }

    [DockerFact]
    public async Task CompleteAsync_vs_CancelAsync_exactly_one_terminal_transition_wins()
    {
        var factory = RequireFactory();
        var completeWins = 0;
        var cancelWins = 0;

        for (var iteration = 0; iteration < CompleteCancelRaceIterations; iteration++)
        {
            var tenantProvider = new FakeTenantProvider();
            var occupied = await SeedConfirmedOccupancyAsync(factory, tenantProvider, includeSlot: true);

            await using var dbComplete = factory.Create(tenantProvider);
            await using var dbCancel = factory.Create(tenantProvider);
            var completeService = CreateReservationService(dbComplete, tenantProvider);
            var cancelService = CreateReservationService(dbCancel, tenantProvider);

            var captured = await Task.WhenAll(
                CaptureAsync(completeService.CompleteAsync(occupied.ReservationId, CancellationToken.None)),
                CaptureAsync(cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None)));

            var completeEx = captured[0];
            var cancelEx = captured[1];
            var completeWon = completeEx is null;
            var cancelWon = cancelEx is null;

            if (completeWon && cancelWon)
            {
                Assert.Fail(
                    "PHASE_A_CONCURRENCY_DEFECT: CompleteAsync and CancelAsync both succeeded on a Confirmed reservation.");
            }

            Assert.True(
                completeWon ^ cancelWon,
                $"Expected exactly one terminal winner; completeEx={completeEx}, cancelEx={cancelEx}.");

            await using var verify = factory.Create(tenantProvider);
            var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
            var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);

            if (completeWon)
            {
                var loser = Assert.IsType<InvalidOperationException>(cancelEx);
                Assert.Equal(ReservationStatus.Completed, reservation.Status);
                Assert.Equal(SlotStatus.Booked, slot.Status);
                Assert.Equal(occupied.ReservationId, slot.ReservationId);
                completeWins++;
                _output.WriteLine(
                    $"iteration {iteration}: Complete won; Cancel threw {loser.Message}");
            }
            else
            {
                var loser = Assert.IsType<InvalidOperationException>(completeEx);
                Assert.Equal(ReservationStatus.Canceled, reservation.Status);
                Assert.Equal(SlotStatus.Available, slot.Status);
                Assert.Null(slot.ReservationId);
                cancelWins++;
                _output.WriteLine(
                    $"iteration {iteration}: Cancel won; Complete threw {loser.Message}");
            }
        }

        Assert.Equal(CompleteCancelRaceIterations, completeWins + cancelWins);
        _output.WriteLine($"Complete wins: {completeWins}; Cancel wins: {cancelWins}");
    }

    [DockerFact]
    public async Task CompleteAsync_then_CancelAsync_keeps_completed_occupancy()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(factory, tenantProvider, includeSlot: true);

        await using var dbComplete = factory.Create(tenantProvider);
        await using var dbCancel = factory.Create(tenantProvider);
        var completeService = CreateReservationService(dbComplete, tenantProvider);
        var cancelService = CreateReservationService(dbCancel, tenantProvider);

        await completeService.CompleteAsync(occupied.ReservationId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None));

        await using var verify = factory.Create(tenantProvider);
        var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
        var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
        Assert.Equal(ReservationStatus.Completed, reservation.Status);
        Assert.Equal(SlotStatus.Booked, slot.Status);
        Assert.Equal(occupied.ReservationId, slot.ReservationId);
    }

    [DockerFact]
    public async Task CancelAsync_then_CompleteAsync_keeps_released_occupancy()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(factory, tenantProvider, includeSlot: true);

        await using var dbCancel = factory.Create(tenantProvider);
        await using var dbComplete = factory.Create(tenantProvider);
        var cancelService = CreateReservationService(dbCancel, tenantProvider);
        var completeService = CreateReservationService(dbComplete, tenantProvider);

        await cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            completeService.CompleteAsync(occupied.ReservationId, CancellationToken.None));

        await using var verify = factory.Create(tenantProvider);
        var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
        var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
        Assert.Equal(ReservationStatus.Canceled, reservation.Status);
        Assert.Equal(SlotStatus.Available, slot.Status);
        Assert.Null(slot.ReservationId);
    }

    [DockerFact]
    public async Task ConfirmAsync_then_CancelAsync_cancels_confirmed_and_releases_occupancy()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(
            factory,
            tenantProvider,
            includeSlot: true,
            status: ReservationStatus.PendingDeposit);

        await using var dbConfirm = factory.Create(tenantProvider);
        await using var dbCancel = factory.Create(tenantProvider);
        var confirmService = CreateReservationService(dbConfirm, tenantProvider);
        var cancelService = CreateReservationService(dbCancel, tenantProvider);

        var confirmed = await confirmService.ConfirmAsync(occupied.ReservationId, CancellationToken.None);
        Assert.Equal(ReservationStatus.Confirmed, confirmed.Status);

        var canceled = await cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, canceled.Status);

        await using var verify = factory.Create(tenantProvider);
        var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
        var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
        Assert.Equal(ReservationStatus.Canceled, reservation.Status);
        Assert.Equal(SlotStatus.Available, slot.Status);
        Assert.Null(slot.ReservationId);
    }

    [DockerFact]
    public async Task CancelAsync_then_ConfirmAsync_rejects_confirm_and_keeps_released_occupancy()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(
            factory,
            tenantProvider,
            includeSlot: true,
            status: ReservationStatus.PendingDeposit);

        await using var dbCancel = factory.Create(tenantProvider);
        await using var dbConfirm = factory.Create(tenantProvider);
        var cancelService = CreateReservationService(dbCancel, tenantProvider);
        var confirmService = CreateReservationService(dbConfirm, tenantProvider);

        var canceled = await cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, canceled.Status);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            confirmService.ConfirmAsync(occupied.ReservationId, CancellationToken.None));
        Assert.Contains(nameof(ReservationStatus.Canceled), ex.Message, StringComparison.Ordinal);

        await using var verify = factory.Create(tenantProvider);
        var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
        var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
        Assert.Equal(ReservationStatus.Canceled, reservation.Status);
        Assert.Equal(SlotStatus.Available, slot.Status);
        Assert.Null(slot.ReservationId);
    }

    [DockerFact]
    public async Task ConfirmAsync_vs_CancelAsync_pending_deposit_does_not_split_occupancy()
    {
        var factory = RequireFactory();
        var confirmSuccess = 0;
        var cancelSuccess = 0;
        var dualSuccess = 0;
        var occupancySplit = 0;
        var confirmOverwroteCancel = 0;
        var cancelLostUpdate = 0;
        var legalConfirmThenCancel = 0;
        var cancelThenConfirmConflict = 0;

        for (var iteration = 0; iteration < ConfirmCancelRaceIterations; iteration++)
        {
            var tenantProvider = new FakeTenantProvider();
            var occupied = await SeedConfirmedOccupancyAsync(
                factory,
                tenantProvider,
                includeSlot: true,
                status: ReservationStatus.PendingDeposit);

            await using var dbConfirm = factory.Create(tenantProvider);
            await using var dbCancel = factory.Create(tenantProvider);
            var confirmService = CreateReservationService(dbConfirm, tenantProvider);
            var cancelService = CreateReservationService(dbCancel, tenantProvider);

            // Alternate start order so Cancel-first (row lock) is not starved by Confirm-listed-first.
            Exception?[] captured;
            if (iteration % 2 == 0)
            {
                captured = await Task.WhenAll(
                    CaptureAsync(cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None)),
                    CaptureAsync(confirmService.ConfirmAsync(occupied.ReservationId, CancellationToken.None)));
            }
            else
            {
                captured = await Task.WhenAll(
                    CaptureAsync(confirmService.ConfirmAsync(occupied.ReservationId, CancellationToken.None)),
                    CaptureAsync(cancelService.CancelAsync(occupied.ReservationId, CancellationToken.None)));
            }

            var cancelEx = iteration % 2 == 0 ? captured[0] : captured[1];
            var confirmEx = iteration % 2 == 0 ? captured[1] : captured[0];
            var confirmWon = confirmEx is null;
            var cancelWon = cancelEx is null;
            if (confirmWon)
            {
                confirmSuccess++;
            }

            if (cancelWon)
            {
                cancelSuccess++;
            }

            if (confirmWon && cancelWon)
            {
                dualSuccess++;
            }

            await using var verify = factory.Create(tenantProvider);
            var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
            var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
            var slotReleased = slot.Status == SlotStatus.Available && slot.ReservationId is null;
            var slotHeld = slot.Status == SlotStatus.Booked && slot.ReservationId == occupied.ReservationId;

            _output.WriteLine(
                $"iteration {iteration} start={(iteration % 2 == 0 ? "cancel-first" : "confirm-first")} " +
                $"confirmOk={confirmWon} cancelOk={cancelWon} status={reservation.Status} " +
                $"slot={slot.Status} slotReservationId={slot.ReservationId} " +
                $"confirmEx={confirmEx?.GetType().Name}:{confirmEx?.Message} " +
                $"cancelEx={cancelEx?.GetType().Name}:{cancelEx?.Message}");

            if (reservation.Status == ReservationStatus.Confirmed && slotReleased)
            {
                occupancySplit++;
                confirmOverwroteCancel++;
            }
            else if (reservation.Status == ReservationStatus.Canceled && slotHeld)
            {
                occupancySplit++;
            }
            else if (confirmWon && cancelWon && reservation.Status == ReservationStatus.Confirmed && slotHeld)
            {
                cancelLostUpdate++;
            }
            else if (confirmWon && cancelWon && reservation.Status == ReservationStatus.Canceled && slotReleased)
            {
                legalConfirmThenCancel++;
            }
            else if (cancelWon && !confirmWon && reservation.Status == ReservationStatus.Canceled && slotReleased)
            {
                var ioe = Assert.IsType<InvalidOperationException>(confirmEx);
                Assert.Contains(nameof(ReservationStatus.Canceled), ioe.Message, StringComparison.Ordinal);
                cancelThenConfirmConflict++;
            }
            else
            {
                Assert.Fail(
                    $"Unclassified Confirm × Cancel serial history at iteration {iteration}: " +
                    $"confirmOk={confirmWon} cancelOk={cancelWon} status={reservation.Status} " +
                    $"slot={slot.Status} slotReservationId={slot.ReservationId}.");
            }
        }

        _output.WriteLine(
            $"confirmSuccess={confirmSuccess} cancelSuccess={cancelSuccess} dualSuccess={dualSuccess} " +
            $"occupancySplit={occupancySplit} confirmOverwroteCancel={confirmOverwroteCancel} " +
            $"cancelLostUpdate={cancelLostUpdate} legalConfirmThenCancel={legalConfirmThenCancel} " +
            $"cancelThenConfirmConflict={cancelThenConfirmConflict}");

        if (occupancySplit > 0 || cancelLostUpdate > 0)
        {
            Assert.Fail(
                "PHASE_A_CONFIRM_CANCEL_CONCURRENCY_DEFECT: Confirm × Cancel split occupancy or lost a write. " +
                $"occupancySplit={occupancySplit} confirmOverwroteCancel={confirmOverwroteCancel} " +
                $"cancelLostUpdate={cancelLostUpdate} dualSuccess={dualSuccess} " +
                $"confirmSuccess={confirmSuccess} cancelSuccess={cancelSuccess}.");
        }

        Assert.Equal(
            ConfirmCancelRaceIterations,
            legalConfirmThenCancel + cancelThenConfirmConflict);
    }

    [DockerFact]
    public async Task CancelByCustomerAsync_vs_CancelAsync_releases_occupancy_once()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(
            factory,
            tenantProvider,
            includeSlot: true,
            occupancyDate: FutureOccupancyDate);

        await using var dbCustomer = factory.Create(tenantProvider);
        await using var dbStaff = factory.Create(tenantProvider);
        var customerService = CreateReservationService(dbCustomer, tenantProvider);
        var staffService = CreateReservationService(dbStaff, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(customerService.CancelByCustomerAsync(
                occupied.Seed.CustomerId,
                occupied.ReservationId,
                CancellationToken.None)),
            CaptureAsync(staffService.CancelAsync(occupied.ReservationId, CancellationToken.None)));

        Assert.True(
            captured.All(ex => ex is null),
            $"Both cancels should succeed (second is idempotent). customerEx={captured[0]} staffEx={captured[1]}");

        await using var verify = factory.Create(tenantProvider);
        var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
        var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
        Assert.Equal(ReservationStatus.Canceled, reservation.Status);
        Assert.Equal(SlotStatus.Available, slot.Status);
        Assert.Null(slot.ReservationId);
        Assert.Equal(0, await CountBlockingAsync(verify));
    }

    [DockerFact]
    public async Task CancelByCustomerAsync_vs_CompleteAsync_exactly_one_terminal_transition_wins()
    {
        var factory = RequireFactory();
        var completeWins = 0;
        var cancelWins = 0;

        for (var iteration = 0; iteration < CompleteCancelRaceIterations; iteration++)
        {
            var tenantProvider = new FakeTenantProvider();
            var occupied = await SeedConfirmedOccupancyAsync(
                factory,
                tenantProvider,
                includeSlot: true,
                occupancyDate: FutureOccupancyDate);

            await using var dbComplete = factory.Create(tenantProvider);
            await using var dbCancel = factory.Create(tenantProvider);
            var completeService = CreateReservationService(dbComplete, tenantProvider);
            var cancelService = CreateReservationService(dbCancel, tenantProvider);

            var captured = await Task.WhenAll(
                CaptureAsync(completeService.CompleteAsync(occupied.ReservationId, CancellationToken.None)),
                CaptureAsync(cancelService.CancelByCustomerAsync(
                    occupied.Seed.CustomerId,
                    occupied.ReservationId,
                    CancellationToken.None)));

            var completeEx = captured[0];
            var cancelEx = captured[1];
            var completeWon = completeEx is null;
            var cancelWon = cancelEx is null;

            if (completeWon && cancelWon)
            {
                Assert.Fail(
                    "B2C_SELF_CANCEL_CONCURRENCY_DEFECT: CompleteAsync and CancelByCustomerAsync both succeeded.");
            }

            Assert.True(
                completeWon ^ cancelWon,
                $"Expected exactly one terminal winner; completeEx={completeEx}, cancelEx={cancelEx}.");

            await using var verify = factory.Create(tenantProvider);
            var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
            var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);

            if (completeWon)
            {
                completeWins++;
                Assert.Equal(ReservationStatus.Completed, reservation.Status);
                Assert.Equal(SlotStatus.Booked, slot.Status);
                Assert.Equal(occupied.ReservationId, slot.ReservationId);
                Assert.IsType<InvalidOperationException>(cancelEx);
            }
            else
            {
                cancelWins++;
                Assert.Equal(ReservationStatus.Canceled, reservation.Status);
                Assert.Equal(SlotStatus.Available, slot.Status);
                Assert.Null(slot.ReservationId);
                Assert.IsType<InvalidOperationException>(completeEx);
            }
        }

        Assert.True(completeWins + cancelWins == CompleteCancelRaceIterations);
    }

    [DockerFact]
    public async Task CancelByCustomerAsync_vs_ConfirmAsync_pending_deposit_does_not_split_occupancy()
    {
        var factory = RequireFactory();
        var confirmThenCancel = 0;
        var cancelThenConfirmConflict = 0;

        for (var iteration = 0; iteration < ConfirmCancelRaceIterations; iteration++)
        {
            var tenantProvider = new FakeTenantProvider();
            var occupied = await SeedConfirmedOccupancyAsync(
                factory,
                tenantProvider,
                includeSlot: true,
                status: ReservationStatus.PendingDeposit,
                occupancyDate: FutureOccupancyDate);

            await using var dbConfirm = factory.Create(tenantProvider);
            await using var dbCancel = factory.Create(tenantProvider);
            var confirmService = CreateReservationService(dbConfirm, tenantProvider);
            var cancelService = CreateReservationService(dbCancel, tenantProvider);

            var captured = iteration % 2 == 0
                ? await Task.WhenAll(
                    CaptureAsync(cancelService.CancelByCustomerAsync(
                        occupied.Seed.CustomerId,
                        occupied.ReservationId,
                        CancellationToken.None)),
                    CaptureAsync(confirmService.ConfirmAsync(occupied.ReservationId, CancellationToken.None)))
                : await Task.WhenAll(
                    CaptureAsync(confirmService.ConfirmAsync(occupied.ReservationId, CancellationToken.None)),
                    CaptureAsync(cancelService.CancelByCustomerAsync(
                        occupied.Seed.CustomerId,
                        occupied.ReservationId,
                        CancellationToken.None)));

            var cancelEx = iteration % 2 == 0 ? captured[0] : captured[1];
            var confirmEx = iteration % 2 == 0 ? captured[1] : captured[0];
            var confirmWon = confirmEx is null;
            var cancelWon = cancelEx is null;

            await using var verify = factory.Create(tenantProvider);
            var reservation = await verify.Reservations.SingleAsync(r => r.Id == occupied.ReservationId);
            var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
            var slotReleased = slot.Status == SlotStatus.Available && slot.ReservationId is null;
            var slotHeld = slot.Status == SlotStatus.Booked && slot.ReservationId == occupied.ReservationId;

            if (confirmWon && cancelWon && reservation.Status == ReservationStatus.Canceled && slotReleased)
            {
                confirmThenCancel++;
            }
            else if (cancelWon && !confirmWon && reservation.Status == ReservationStatus.Canceled && slotReleased)
            {
                var ioe = Assert.IsType<InvalidOperationException>(confirmEx);
                Assert.Contains(nameof(ReservationStatus.Canceled), ioe.Message, StringComparison.Ordinal);
                cancelThenConfirmConflict++;
            }
            else if (reservation.Status == ReservationStatus.Confirmed && slotReleased)
            {
                Assert.Fail(
                    $"B2C_SELF_CANCEL_CONCURRENCY_DEFECT: Confirm overwrote cancel occupancy at iteration {iteration}.");
            }
            else
            {
                Assert.Fail(
                    $"Unclassified Customer Cancel × Confirm at iteration {iteration}: " +
                    $"confirmOk={confirmWon} cancelOk={cancelWon} status={reservation.Status} " +
                    $"slot={slot.Status} slotReservationId={slot.ReservationId}.");
            }
        }

        Assert.Equal(
            ConfirmCancelRaceIterations,
            confirmThenCancel + cancelThenConfirmConflict);
    }

    [DockerFact]
    public async Task CancelAsync_staff_after_start_still_succeeds()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var occupied = await SeedConfirmedOccupancyAsync(
            factory,
            tenantProvider,
            includeSlot: true,
            occupancyDate: PastOccupancyDate);

        await using var db = factory.Create(tenantProvider);
        var start = await db.Reservations
            .Where(r => r.Id == occupied.ReservationId)
            .Select(r => r.StartDateTime)
            .SingleAsync();
        Assert.True(start <= DateTimeOffset.UtcNow);

        var service = CreateReservationService(db, tenantProvider);
        var result = await service.CancelAsync(occupied.ReservationId, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, result.Status);

        await using var verify = factory.Create(tenantProvider);
        var slot = await verify.Slots.SingleAsync(s => s.Id == occupied.Seed.SlotId);
        Assert.Equal(SlotStatus.Available, slot.Status);
        Assert.Null(slot.ReservationId);
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static async Task<SeededLocation> SeedLocationAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        bool includeSlot,
        DateOnly? occupancyDate = null)
    {
        var date = occupancyDate ?? Date;
        await using var db = factory.Create(tenantProvider);

        var tenant = new Tenant("Clube Concurrency", UniqueTaxId(), subdomain: $"lock-{Guid.NewGuid():N}"[..20]);
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
            SchedulePolicy = SchedulePolicy.OpenHours,
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
            DayOfWeek = date.DayOfWeek,
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

        Guid? slotId = null;
        if (includeSlot)
        {
            var kind = new OccupancyKind
            {
                TenantId = tenant.Id,
                Key = "open",
                Label = "Aberto",
                IsBookableByCustomer = true,
                BlocksCapacity = true,
                SortOrder = 0,
                IsActive = true,
            };
            var slot = new Slot
            {
                TenantId = tenant.Id,
                RentalAssetId = rental.Id,
                Date = date,
                StartTime = Start,
                EndTime = End,
                OccupancyKindId = kind.Id,
                Status = SlotStatus.Available,
            };
            db.OccupancyKinds.Add(kind);
            db.Slots.Add(slot);
            slotId = slot.Id;
        }

        await db.SaveChangesAsync();

        return new SeededLocation(tenant.Id, unit.Id, customer.Id, asset.Id, rental.Id, slotId);
    }

    private static ReservationService CreateReservationService(
        AppDbContext db,
        FakeTenantProvider tenantProvider) =>
        new(db, tenantProvider, new FakeTrialGuard(), TestReservationQueue.Create(db, tenantProvider));

    private static CreateReservationRequestDto CreateRequest(SeededLocation seed) =>
        new()
        {
            UnitId = seed.UnitId,
            Date = Date,
            StartTime = Start,
            EndTime = End,
            Items =
            [
                new CreateReservationItemRequestDto { AssetId = seed.AssetId, Quantity = 1 }
            ]
        };

    private static CreateReservationRequestDto CreateMultiItemRequest(SeededTwoLocations seed) =>
        new()
        {
            UnitId = seed.UnitId,
            Date = Date,
            StartTime = Start,
            EndTime = End,
            Items =
            [
                new CreateReservationItemRequestDto { AssetId = seed.AssetIdA, Quantity = 1 },
                new CreateReservationItemRequestDto { AssetId = seed.AssetIdB, Quantity = 1 },
            ]
        };

    private static async Task<SeededOccupied> SeedConfirmedOccupancyAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        bool includeSlot,
        ReservationStatus status = ReservationStatus.Confirmed,
        DateOnly? occupancyDate = null)
    {
        var date = occupancyDate ?? Date;
        var seed = await SeedLocationAsync(factory, tenantProvider, includeSlot, date);
        await using var db = factory.Create(tenantProvider);

        var reservation = new Reservation
        {
            TenantId = seed.TenantId,
            UnitId = seed.UnitId,
            CustomerId = seed.CustomerId,
            CustomerName = "Existing",
            CustomerWhatsApp = "11999999999",
            StartDateTime = BrazilTimeZone.AtLocal(date, Start),
            EndDateTime = BrazilTimeZone.AtLocal(date, End),
            Status = status,
            TotalAmount = 100m,
            DepositPaid = 0m,
        };
        reservation.AddItem(new ReservationItem
        {
            TenantId = seed.TenantId,
            ReservationId = reservation.Id,
            RentalAssetId = seed.RentalAssetId,
            Quantity = 1,
            UnitPrice = 100m,
            SubTotal = 100m,
        });
        db.Reservations.Add(reservation);

        if (seed.SlotId is Guid slotId)
        {
            var slot = await db.Slots.SingleAsync(s => s.Id == slotId);
            slot.MarkBooked(reservation.Id);
        }

        await db.SaveChangesAsync();
        return new SeededOccupied(seed, reservation.Id);
    }

    private static async Task<SeededTwoLocations> SeedTwoLocationsAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider)
    {
        await using var db = factory.Create(tenantProvider);

        var tenant = new Tenant("Clube Multi", UniqueTaxId(), subdomain: $"multi-{Guid.NewGuid():N}"[..20]);
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = "{}",
        };
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Cliente B2C",
            Email = "cliente@club.test",
            Phone = "11999999999",
        };

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Customers.Add(customer);

        var first = AddLocation(db, tenant.Id, unit.Id, category.Id, family.Id, 1);
        var second = AddLocation(db, tenant.Id, unit.Id, category.Id, family.Id, 2);
        await db.SaveChangesAsync();

        // CreateReservationAsync locks OrderBy(r => r.Id). Persist so Ids exist, then
        // expose them in that same order regardless of how items are listed on the DTO.
        var ordered = new[] { first, second }.OrderBy(x => x.RentalAssetId).ToArray();
        return new SeededTwoLocations(
            tenant.Id,
            unit.Id,
            customer.Id,
            ordered[0].AssetId,
            ordered[0].RentalAssetId,
            ordered[1].AssetId,
            ordered[1].RentalAssetId);
    }

    private static (Guid AssetId, Guid RentalAssetId) AddLocation(
        AppDbContext db,
        Guid tenantId,
        Guid unitId,
        Guid categoryId,
        Guid familyId,
        int index)
    {
        var asset = new Asset
        {
            TenantId = tenantId,
            UnitId = unitId,
            CategoryId = categoryId,
            FamilyId = familyId,
            Name = $"Quadra {index}",
            Tag = $"Q{index}",
            Status = AssetStatus.Active,
            IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = tenantId,
            AssetId = asset.Id,
            Type = RentalAssetType.Location,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = true,
            SchedulePolicy = SchedulePolicy.OpenHours,
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0),
            QueueEnabled = false,
        };
        var pricing = new RentalPricing
        {
            TenantId = tenantId,
            RentalAssetId = rental.Id,
            DayOfWeek = Date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(22, 0),
            PricePerHour = 100m,
            RequiresDeposit = true,
            DepositPercentage = 50m,
        };
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.RentalPricings.Add(pricing);
        return (asset.Id, rental.Id);
    }

    private static async Task<Guid> SeedConfirmedMultiItemAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        SeededTwoLocations seed)
    {
        await using var db = factory.Create(tenantProvider);
        var reservation = new Reservation
        {
            TenantId = seed.TenantId,
            UnitId = seed.UnitId,
            CustomerId = seed.CustomerId,
            CustomerName = "Existing",
            CustomerWhatsApp = "11999999999",
            StartDateTime = BrazilTimeZone.AtLocal(Date, Start),
            EndDateTime = BrazilTimeZone.AtLocal(Date, End),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 200m,
            DepositPaid = 0m,
        };
        // Insert items in reverse Id order so cancel must OrderBy(rentalAssetId) to match create.
        reservation.AddItem(new ReservationItem
        {
            TenantId = seed.TenantId,
            ReservationId = reservation.Id,
            RentalAssetId = seed.RentalAssetIdB,
            Quantity = 1,
            UnitPrice = 100m,
            SubTotal = 100m,
        });
        reservation.AddItem(new ReservationItem
        {
            TenantId = seed.TenantId,
            ReservationId = reservation.Id,
            RentalAssetId = seed.RentalAssetIdA,
            Quantity = 1,
            UnitPrice = 100m,
            SubTotal = 100m,
        });
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();
        return reservation.Id;
    }

    private static void AssertSingleWinner(Exception?[] captured)
    {
        var successes = captured.Count(ex => ex is null);
        var failure = Assert.Single(captured, ex => ex is not null);
        Assert.Equal(1, successes);

        var ioe = Assert.IsType<InvalidOperationException>(failure);
        Assert.True(
            ioe.Message.Contains("not available", StringComparison.OrdinalIgnoreCase)
            || ioe.Message.Contains("already reserved", StringComparison.OrdinalIgnoreCase),
            ioe.Message);
    }

    private static Task<int> CountBlockingAsync(AppDbContext db) =>
        db.Reservations.CountAsync(r =>
            r.Status == ReservationStatus.PendingDeposit
            || r.Status == ReservationStatus.Confirmed);

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

    private static string UniqueTaxId()
    {
        var digits = Guid.NewGuid().ToString("N")[..14];
        return digits;
    }

    private sealed record SeededLocation(
        Guid TenantId,
        Guid UnitId,
        Guid CustomerId,
        Guid AssetId,
        Guid RentalAssetId,
        Guid? SlotId);

    private sealed record SeededOccupied(SeededLocation Seed, Guid ReservationId);

    private sealed record SeededTwoLocations(
        Guid TenantId,
        Guid UnitId,
        Guid CustomerId,
        Guid AssetIdA,
        Guid RentalAssetIdA,
        Guid AssetIdB,
        Guid RentalAssetIdB);

    private sealed class UnusedOccupancyKindService : IOccupancyKindService
    {
        public Task<IReadOnlyList<OccupancyKindResponseDto>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OccupancyKindResponseDto> CreateAsync(
            UpsertOccupancyKindRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OccupancyKindResponseDto> UpdateAsync(
            Guid id,
            UpsertOccupancyKindRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnsureDefaultsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
