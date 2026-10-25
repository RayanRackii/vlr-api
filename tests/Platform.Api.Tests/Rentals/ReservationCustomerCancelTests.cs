using Microsoft.EntityFrameworkCore;
using Platform.Api.Tests.Infrastructure;
using Platform.Api.Modules.Rentals;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationCustomerCancelTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Customer_cannot_cancel_at_or_after_the_cutoff(int secondsPastDeadline)
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var now = LocationBookingHarness.BookingClock.GetUtcNow();
        var start = now.AddHours(24).AddSeconds(-secondsPastDeadline);
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Confirmed, start, start.AddHours(1));
        harness.Db.BookingPolicies.Add(new BookingPolicy { TenantId = harness.TenantId, CancelCutoffHours = 24 });
        var slot = harness.SeedBookedSlot(reservation);
        await harness.Db.SaveChangesAsync();

        if (secondsPastDeadline < 0)
        {
            var result = await harness.CreateReservationService().CancelByCustomerAsync(
                harness.CustomerId, reservation.Id, CancellationToken.None);
            Assert.Equal(ReservationStatus.Canceled, result.Status);
        }
        else
        {
            var error = await Assert.ThrowsAsync<CancelCutoffPassedException>(() =>
                harness.CreateReservationService().CancelByCustomerAsync(
                    harness.CustomerId, reservation.Id, CancellationToken.None));
            Assert.Equal(24, error.CancelCutoffHours);
            Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
            Assert.Equal(SlotStatus.Booked, slot.Status);
        }
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(24, null, 24)]
    [InlineData(24, 0, null)]
    [InlineData(24, 48, 48)]
    public async Task Responses_expose_effective_cancel_deadline(int? tenantCutoff, int? resourceCutoff, int? effectiveCutoff)
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var start = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Confirmed, start, start.AddHours(1));
        harness.Db.BookingPolicies.Add(new BookingPolicy { TenantId = harness.TenantId, CancelCutoffHours = tenantCutoff });
        var rental = await harness.Db.RentalAssets.SingleAsync(r => r.Id == harness.RentalAssetId);
        rental.CancelCutoffHours = resourceCutoff;
        await harness.Db.SaveChangesAsync();

        var service = harness.CreateReservationService();
        var response = Assert.Single(await service.ListMineAsync(harness.CustomerId, CancellationToken.None));
        DateTimeOffset? expected = effectiveCutoff switch
        {
            24 => new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            48 => new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            _ => null,
        };
        Assert.Equal(expected, response.CancelDeadlineUtc);
        var canceled = await service.CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);
        Assert.Equal(expected, canceled.CancelDeadlineUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multi_item_uses_strictest_cutoff_and_staff_is_exempt(bool staff)
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var start = LocationBookingHarness.BookingClock.GetUtcNow().AddHours(36);
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Confirmed, start, start.AddHours(1));
        harness.Db.BookingPolicies.Add(new BookingPolicy { TenantId = harness.TenantId, CancelCutoffHours = 24 });
        var first = await harness.Db.RentalAssets.SingleAsync(r => r.Id == harness.RentalAssetId);
        first.CancelCutoffHours = 0;
        var secondId = harness.SeedGoodRentalAsset(2);
        var second = harness.Db.RentalAssets.Local.Single(r => r.Id == secondId);
        second.CancelCutoffHours = 48;
        reservation.AddItem(new ReservationItem
        {
            TenantId = harness.TenantId, ReservationId = reservation.Id, RentalAssetId = secondId,
            Quantity = 1, UnitPrice = 10m, SubTotal = 10m,
        });
        await harness.Db.SaveChangesAsync();
        var service = harness.CreateReservationService();
        if (staff)
        {
            var result = await service.CancelAsync(reservation.Id, CancellationToken.None);
            Assert.Equal(ReservationStatus.Canceled, result.Status);
        }
        else
        {
            var error = await Assert.ThrowsAsync<CancelCutoffPassedException>(() =>
                service.CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None));
            Assert.Equal(48, error.CancelCutoffHours);
            Assert.Equal(ReservationStatus.Confirmed, reservation.Status);
        }
        var response = Assert.Single(await service.ListMineAsync(harness.CustomerId, CancellationToken.None));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), response.CancelDeadlineUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task Null_or_zero_cutoff_preserves_customer_cancellation(int? cutoff)
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var start = LocationBookingHarness.BookingClock.GetUtcNow().AddHours(1);
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Confirmed, start, start.AddHours(1));
        harness.Db.BookingPolicies.Add(new BookingPolicy { TenantId = harness.TenantId, CancelCutoffHours = cutoff });
        await harness.Db.SaveChangesAsync();
        var result = await harness.CreateReservationService().CancelByCustomerAsync(
            harness.CustomerId, reservation.Id, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, result.Status);
        Assert.Null(result.CancelDeadlineUtc);
    }

    [Fact]
    public async Task Explicit_zero_resource_override_disables_tenant_cutoff_and_canceled_retries_stay_idempotent()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var start = LocationBookingHarness.BookingClock.GetUtcNow().AddHours(1);
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.PendingDeposit, start, start.AddHours(1));
        harness.Db.BookingPolicies.Add(new BookingPolicy { TenantId = harness.TenantId, CancelCutoffHours = 24 });
        var rental = await harness.Db.RentalAssets.SingleAsync(r => r.Id == harness.RentalAssetId);
        rental.CancelCutoffHours = 0;
        await harness.Db.SaveChangesAsync();
        var service = harness.CreateReservationService();
        var result = await service.CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, result.Status);
        Assert.Null(result.CancelDeadlineUtc);
        rental.CancelCutoffHours = null;
        await harness.Db.SaveChangesAsync();
        var retry = await service.CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);
        Assert.Equal(ReservationStatus.Canceled, retry.Status);
    }

    [Fact]
    public async Task CancelByCustomerAsync_owner_pending_deposit_future_cancels()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.PendingDeposit,
            start,
            end);
        await harness.Db.SaveChangesAsync();

        var result = await harness.CreateReservationService()
            .CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Canceled, result.Status);
        Assert.Equal(
            ReservationStatus.Canceled,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task CancelByCustomerAsync_owner_confirmed_future_cancels()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Confirmed,
            start,
            end);
        await harness.Db.SaveChangesAsync();

        var result = await harness.CreateReservationService()
            .CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Canceled, result.Status);
    }

    [Fact]
    public async Task CancelByCustomerAsync_releases_location_slot()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Confirmed,
            start,
            end);
        var slot = harness.SeedBookedSlot(reservation);
        await harness.Db.SaveChangesAsync();

        await harness.CreateReservationService()
            .CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);

        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == slot.Id);
        Assert.Equal(SlotStatus.Available, persisted.Status);
        Assert.Null(persisted.ReservationId);
    }

    [Fact]
    public async Task CancelByCustomerAsync_releases_good_quantity()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var goodId = harness.SeedGoodRentalAsset(totalQuantity: 4);
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Confirmed,
            start,
            end,
            rentalAssetId: goodId,
            quantity: 2);
        await harness.Db.SaveChangesAsync();

        var service = harness.CreateReservationService();
        Assert.Equal(
            2,
            await service.GetReservedQuantityAsync(harness.TenantId, goodId, start, end, excludeReservationId: null, CancellationToken.None));

        await service.CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);

        Assert.Equal(
            0,
            await service.GetReservedQuantityAsync(harness.TenantId, goodId, start, end, excludeReservationId: null, CancellationToken.None));
    }

    [Fact]
    public async Task CancelByCustomerAsync_other_customer_throws_not_found()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Confirmed,
            start,
            end);
        var other = harness.SeedCustomer("Outro", "outro@club.test");
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.CreateReservationService().CancelByCustomerAsync(
                other.Id,
                reservation.Id,
                CancellationToken.None));

        Assert.Equal("Reservation not found.", ex.Message);
        Assert.Equal(
            ReservationStatus.Confirmed,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task CancelByCustomerAsync_missing_throws_not_found()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.CreateReservationService().CancelByCustomerAsync(
                harness.CustomerId,
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Equal("Reservation not found.", ex.Message);
    }

    [Fact]
    public async Task CancelByCustomerAsync_started_throws()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = PastWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Confirmed,
            start,
            end);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateReservationService().CancelByCustomerAsync(
                harness.CustomerId,
                reservation.Id,
                CancellationToken.None));

        Assert.Equal("Cannot cancel a reservation that has already started.", ex.Message);
        Assert.Equal(
            ReservationStatus.Confirmed,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task CancelByCustomerAsync_completed_throws()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Completed,
            start,
            end);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateReservationService().CancelByCustomerAsync(
                harness.CustomerId,
                reservation.Id,
                CancellationToken.None));

        Assert.Equal("Cannot cancel a completed reservation.", ex.Message);
        Assert.Equal(
            ReservationStatus.Completed,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task CancelByCustomerAsync_canceled_is_idempotent()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Canceled,
            start,
            end);
        await harness.Db.SaveChangesAsync();

        var result = await harness.CreateReservationService()
            .CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Canceled, result.Status);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task CancelByCustomerAsync_other_customer_still_404_when_already_canceled()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Canceled,
            start,
            end);
        var other = harness.SeedCustomer("Outro", "outro@club.test");
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.CreateReservationService().CancelByCustomerAsync(
                other.Id,
                reservation.Id,
                CancellationToken.None));

        Assert.Equal("Reservation not found.", ex.Message);
    }

    [Fact]
    public async Task CancelAsync_staff_after_start_still_succeeds()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        var slot = harness.SeedBookedSlot(reservation);
        await harness.Db.SaveChangesAsync();
        Assert.True(reservation.StartDateTime <= DateTimeOffset.UtcNow);

        var result = await harness.CreateReservationService()
            .CancelAsync(reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Canceled, result.Status);
        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == slot.Id);
        Assert.Equal(SlotStatus.Available, persisted.Status);
        Assert.Null(persisted.ReservationId);
    }

    [Fact]
    public async Task CancelByCustomerAsync_does_not_restore_completed_queue_ticket()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var (start, end) = FutureWindow();
        var reservation = harness.SeedOverlappingReservation(
            ReservationStatus.Confirmed,
            start,
            end);
        var session = new ReservationQueueSession
        {
            TenantId = harness.TenantId,
            RentalAssetId = harness.RentalAssetId,
            OpeningDate = LocationBookingHarness.Date,
            OpensAt = start.AddHours(-3),
            WaitingRoomOpensAt = start.AddHours(-4),
        };
        var ticket = new ReservationQueueTicket
        {
            TenantId = harness.TenantId,
            QueueSessionId = session.Id,
            CustomerId = harness.CustomerId,
            Sequence = 1,
            Status = QueueTicketStatus.Completed,
            JoinedAt = start.AddHours(-2),
            CompletedReservationId = reservation.Id,
        };
        session.AddTicket(ticket);
        harness.Db.ReservationQueueSessions.Add(session);
        harness.Db.ReservationQueueTickets.Add(ticket);
        await harness.Db.SaveChangesAsync();

        await harness.CreateReservationService()
            .CancelByCustomerAsync(harness.CustomerId, reservation.Id, CancellationToken.None);

        var persisted = await harness.Db.ReservationQueueTickets.SingleAsync(t => t.Id == ticket.Id);
        Assert.Equal(QueueTicketStatus.Completed, persisted.Status);
        Assert.Equal(reservation.Id, persisted.CompletedReservationId);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) FutureWindow()
    {
        var start = LocationBookingHarness.BookingClock.GetUtcNow().AddDays(14);
        return (start, start.AddHours(1));
    }

    private static (DateTimeOffset Start, DateTimeOffset End) PastWindow()
    {
        var end = LocationBookingHarness.BookingClock.GetUtcNow().AddMinutes(-5);
        return (end.AddHours(-1), end);
    }
}
