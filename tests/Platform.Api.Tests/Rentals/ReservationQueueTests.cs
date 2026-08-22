using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationQueueTests
{
    [Fact]
    public async Task Queue_disabled_create_reservation_works_like_today()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync(queueEnabled: false);
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(6, 0));

        var created = await harness.Reservations().CreateReservationAsync(
            harness.CustomerA,
            harness.BookRequest(),
            CancellationToken.None);

        Assert.Equal(ReservationStatus.PendingDeposit, created.Status);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Waiting_room_closed_join_throws_QUEUE_WAITING_ROOM_CLOSED()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(6, 0));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None));

        Assert.Equal(ReservationQueueCodes.WaitingRoomClosed, ex.Message);
    }

    [Fact]
    public async Task Waiting_room_open_join_is_Waiting_and_cannot_book()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));

        var status = await harness.Queue().JoinAsync(
            harness.RentalAssetId,
            harness.CustomerA,
            CancellationToken.None);

        Assert.Equal(QueuePhase.WaitingRoom, status.Phase);
        Assert.NotNull(status.MyTicket);
        Assert.Equal(QueueTicketStatus.Waiting, status.MyTicket.Status);
        Assert.Equal(1, status.MyTicket.Sequence);

        var book = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Reservations().CreateReservationAsync(
                harness.CustomerA,
                harness.BookRequest(),
                CancellationToken.None));
        Assert.Equal(ReservationQueueCodes.Waiting, book.Message);
        Assert.Equal(0, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Duplicate_join_returns_same_ticket()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));

        var first = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        var second = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);

        Assert.Equal(first.MyTicket!.Id, second.MyTicket!.Id);
        Assert.Equal(first.MyTicket.Sequence, second.MyTicket.Sequence);
        Assert.Equal(1, await harness.Db.ReservationQueueTickets.CountAsync());
    }

    [Fact]
    public async Task Join_order_A_B_C_preserves_sequences_1_2_3()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));

        var a = await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        var b = await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerB, CancellationToken.None);
        var c = await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerC, CancellationToken.None);

        Assert.Equal(1, a.MyTicket!.Sequence);
        Assert.Equal(2, b.MyTicket!.Sequence);
        Assert.Equal(3, c.MyTicket!.Sequence);
        Assert.Equal(1, a.MyTicket.Position);
        Assert.Equal(2, b.MyTicket.Position);
        Assert.Equal(3, c.MyTicket.Position);
        Assert.Equal(2, c.AheadCount);
    }

    [Fact]
    public async Task Only_first_becomes_Active_after_T()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));
        await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerB, CancellationToken.None);
        await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerC, CancellationToken.None);

        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var a = await harness.Queue().GetStatusAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        var b = await harness.Queue().GetStatusAsync(harness.RentalAssetId, harness.CustomerB, CancellationToken.None);
        var c = await harness.Queue().GetStatusAsync(harness.RentalAssetId, harness.CustomerC, CancellationToken.None);

        Assert.Equal(QueuePhase.Open, a.Phase);
        Assert.Equal(QueueTicketStatus.Active, a.MyTicket!.Status);
        Assert.Equal(QueueTicketStatus.Waiting, b.MyTicket!.Status);
        Assert.Equal(QueueTicketStatus.Waiting, c.MyTicket!.Status);
        Assert.Equal(1, await harness.Db.ReservationQueueTickets.CountAsync(t => t.Status == QueueTicketStatus.Active));
    }

    [Fact]
    public async Task Waiting_cannot_book_after_T()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));
        await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerB, CancellationToken.None);
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        await harness.Queue().GetStatusAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Reservations().CreateReservationAsync(
                harness.CustomerB,
                harness.BookRequest(),
                CancellationToken.None));
        Assert.Equal(ReservationQueueCodes.Waiting, ex.Message);
    }

    [Fact]
    public async Task Active_can_book_once_and_failed_validation_does_not_complete()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var joined = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        Assert.Equal(QueueTicketStatus.Active, joined.MyTicket!.Status);

        var overlapping = new Reservation
        {
            TenantId = harness.TenantId,
            UnitId = harness.UnitId,
            CustomerId = harness.CustomerB,
            CustomerName = "Other",
            CustomerWhatsApp = "11988880000",
            StartDateTime = new DateTimeOffset(
                ReservationQueueHarness.BookDate.ToDateTime(ReservationQueueHarness.BookStart),
                TimeSpan.Zero),
            EndDateTime = new DateTimeOffset(
                ReservationQueueHarness.BookDate.ToDateTime(ReservationQueueHarness.BookEnd),
                TimeSpan.Zero),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 100m,
            DepositPaid = 0m,
        };
        overlapping.AddItem(new ReservationItem
        {
            TenantId = harness.TenantId,
            ReservationId = overlapping.Id,
            RentalAssetId = harness.RentalAssetId,
            Quantity = 1,
            UnitPrice = 100m,
            SubTotal = 100m,
        });
        harness.Db.Reservations.Add(overlapping);
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Reservations().CreateReservationAsync(
                harness.CustomerA,
                harness.BookRequest(),
                CancellationToken.None));

        var stillActive = await harness.Queue().GetStatusAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        Assert.Equal(QueueTicketStatus.Active, stillActive.MyTicket!.Status);
        Assert.Equal(joined.MyTicket.Id, stillActive.MyTicket.Id);

        var laterRequest = new CreateReservationRequestDto
        {
            UnitId = harness.UnitId,
            Date = ReservationQueueHarness.BookDate,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
            Items =
            [
                new CreateReservationItemRequestDto { AssetId = harness.AssetId, Quantity = 1 }
            ]
        };

        var created = await harness.Reservations().CreateReservationAsync(
            harness.CustomerA,
            laterRequest,
            CancellationToken.None);
        Assert.Single(created.Items);

        var completed = await harness.Db.ReservationQueueTickets.SingleAsync(t => t.Id == joined.MyTicket.Id);
        Assert.Equal(QueueTicketStatus.Completed, completed.Status);
        Assert.Equal(created.Id, completed.CompletedReservationId);

        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Reservations().CreateReservationAsync(
                harness.CustomerA,
                laterRequest,
                CancellationToken.None));
        Assert.Equal(ReservationQueueCodes.TurnAlreadyUsed, second.Message);
    }

    [Fact]
    public async Task Timeout_expires_Active_and_promotes_next()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));
        await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerB, CancellationToken.None);

        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var active = await harness.Queue().GetStatusAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        Assert.Equal(QueueTicketStatus.Active, active.MyTicket!.Status);
        var expiresAt = active.MyTicket.TurnExpiresAt;
        Assert.NotNull(expiresAt);

        harness.Time.SetUtcNow(expiresAt.Value.AddSeconds(1));
        var after = await harness.Queue().GetStatusAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        var next = await harness.Queue().GetStatusAsync(
            harness.RentalAssetId, harness.CustomerB, CancellationToken.None);

        Assert.Equal(QueueTicketStatus.Expired, after.MyTicket!.Status);
        Assert.Equal(active.MyTicket.Id, after.MyTicket.Id);
        Assert.Equal(QueueTicketStatus.Active, next.MyTicket!.Status);
        Assert.Equal(2, next.MyTicket.Sequence);
    }

    [Fact]
    public async Task Expired_cannot_book()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var joined = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        harness.Time.SetUtcNow(joined.MyTicket!.TurnExpiresAt!.Value.AddSeconds(1));
        await harness.Queue().GetStatusAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Reservations().CreateReservationAsync(
                harness.CustomerA,
                harness.BookRequest(),
                CancellationToken.None));
        Assert.Equal(ReservationQueueCodes.TurnExpired, ex.Message);
    }

    [Fact]
    public async Task Rejoin_after_Expired_gets_new_higher_sequence()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));
        var first = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        await harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerB, CancellationToken.None);

        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        await harness.Queue().GetStatusAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        var a = await harness.Db.ReservationQueueTickets.SingleAsync(t => t.Id == first.MyTicket!.Id);
        harness.Time.SetUtcNow(a.TurnExpiresAt!.Value.AddSeconds(1));
        await harness.Queue().GetStatusAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None);

        var rejoin = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        Assert.NotEqual(first.MyTicket!.Id, rejoin.MyTicket!.Id);
        Assert.True(rejoin.MyTicket.Sequence > first.MyTicket.Sequence);
        Assert.Equal(QueueTicketStatus.Waiting, rejoin.MyTicket.Status);
    }

    [Fact]
    public async Task Reconnect_GET_keeps_ticket_id_and_TurnExpiresAt()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var joined = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        var expiresAt = joined.MyTicket!.TurnExpiresAt;
        Assert.NotNull(expiresAt);

        harness.Time.Advance(TimeSpan.FromSeconds(20));
        var again = await harness.Queue().GetStatusAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);

        Assert.Equal(joined.MyTicket.Id, again.MyTicket!.Id);
        Assert.Equal(expiresAt, again.MyTicket.TurnExpiresAt);
        Assert.Equal(QueueTicketStatus.Active, again.MyTicket.Status);
    }

    [Fact]
    public async Task Tenant_isolation_hides_other_tenant_tickets()
    {
        var databaseName = $"queue-iso-{Guid.NewGuid():N}";
        await using var tenantA = await ReservationQueueHarness.CreateAsync(databaseName: databaseName);
        tenantA.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));
        await tenantA.Queue().JoinAsync(tenantA.RentalAssetId, tenantA.CustomerA, CancellationToken.None);

        var providerB = new FakeTenantProvider();
        await using var tenantB = await ReservationQueueHarness.CreateAsync(
            databaseName: databaseName,
            tenantProvider: providerB);
        tenantB.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 10));

        Assert.Empty(await tenantB.Db.ReservationQueueTickets.ToListAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            tenantB.Queue().GetStatusAsync(tenantA.RentalAssetId, tenantB.CustomerA, CancellationToken.None));
    }

    [Fact]
    public async Task CreateReservation_bypass_without_ticket_throws_QUEUE_REQUIRED()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Reservations().CreateReservationAsync(
                harness.CustomerA,
                harness.BookRequest(),
                CancellationToken.None));
        Assert.Equal(ReservationQueueCodes.Required, ex.Message);
        Assert.Equal(0, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Join_after_T_with_empty_queue_promotes_immediately_to_Active()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));

        var status = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);

        Assert.Equal(QueuePhase.Open, status.Phase);
        Assert.Equal(QueueTicketStatus.Active, status.MyTicket!.Status);
        Assert.NotNull(status.MyTicket.TurnExpiresAt);
    }

    [Fact]
    public async Task Queue_off_or_missing_location_is_not_found()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync(queueEnabled: false);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.Queue().JoinAsync(harness.RentalAssetId, harness.CustomerA, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.Queue().GetStatusAsync(Guid.NewGuid(), harness.CustomerA, CancellationToken.None));
    }

    [Fact]
    public async Task Good_cannot_enable_queue()
    {
        await using var assets = await BulkCreateAssetsHarness.CreateAsync();
        var service = assets.CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(
                new CreateAssetRequest
                {
                    UnitId = assets.UnitId,
                    CategoryId = assets.CategoryId,
                    FamilyId = assets.FamilyId,
                    Name = "Raquete",
                    Tag = "RQ-1",
                    IsRentable = true,
                    RentalType = RentalAssetType.Good,
                    TotalQuantity = 10,
                    QueueEnabled = true,
                    QueueOpeningTime = new TimeOnly(7, 30),
                },
                CancellationToken.None));

        Assert.Contains("Location", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteTurn_after_TurnExpiresAt_throws_QUEUE_TURN_EXPIRED()
    {
        await using var harness = await ReservationQueueHarness.CreateAsync();
        harness.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var joined = await harness.Queue().JoinAsync(
            harness.RentalAssetId, harness.CustomerA, CancellationToken.None);
        var rental = await harness.Db.RentalAssets.SingleAsync(r => r.Id == harness.RentalAssetId);

        harness.Time.SetUtcNow(joined.MyTicket!.TurnExpiresAt!.Value.AddSeconds(1));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Queue().CompleteTurnAsync(
                harness.CustomerA,
                rental,
                Guid.NewGuid(),
                CancellationToken.None));
        Assert.Equal(ReservationQueueCodes.TurnExpired, ex.Message);

        var ticket = await harness.Db.ReservationQueueTickets.SingleAsync(t => t.Id == joined.MyTicket.Id);
        Assert.Equal(QueueTicketStatus.Expired, ticket.Status);
        Assert.Null(ticket.CompletedReservationId);
    }

    [Fact]
    public void Clock_closed_before_waiting_room_uses_today()
    {
        var now = ReservationQueueHarness.Brazil(6, 0);
        var opening = ReservationQueueHarness.OpeningTime;
        var date = ReservationQueueClock.ResolveOpeningDate(now, opening);
        Assert.Equal(ReservationQueueHarness.OpeningDate, date);

        var wr = ReservationQueueClock.WaitingRoomOpensAt(date, opening);
        var opens = ReservationQueueClock.OpensAt(date, opening);
        Assert.Equal(QueuePhase.Closed, ReservationQueueClock.ResolvePhase(now, wr, opens));
        Assert.Equal(QueuePhase.WaitingRoom, ReservationQueueClock.ResolvePhase(
            ReservationQueueHarness.Brazil(7, 10), wr, opens));
        Assert.Equal(QueuePhase.Open, ReservationQueueClock.ResolvePhase(
            ReservationQueueHarness.Brazil(7, 31), wr, opens));
    }

    [Fact]
    public void Clock_civil_midnight_closes_until_next_waiting_room()
    {
        var opening = ReservationQueueHarness.OpeningTime;
        var late = ReservationQueueHarness.Brazil(23, 59);
        var lateDate = ReservationQueueClock.ResolveOpeningDate(late, opening);
        Assert.Equal(ReservationQueueHarness.OpeningDate, lateDate);
        Assert.Equal(
            QueuePhase.Open,
            ReservationQueueClock.ResolvePhase(
                late,
                ReservationQueueClock.WaitingRoomOpensAt(lateDate, opening),
                ReservationQueueClock.OpensAt(lateDate, opening)));

        var nextDay = ReservationQueueHarness.OpeningDate.AddDays(1);
        var midnight = ReservationQueueHarness.Brazil(nextDay, 0, 0);
        var midnightDate = ReservationQueueClock.ResolveOpeningDate(midnight, opening);
        Assert.Equal(nextDay, midnightDate);
        Assert.Equal(
            QueuePhase.Closed,
            ReservationQueueClock.ResolvePhase(
                midnight,
                ReservationQueueClock.WaitingRoomOpensAt(midnightDate, opening),
                ReservationQueueClock.OpensAt(midnightDate, opening)));
    }

    [Fact]
    public void Clock_opening_near_midnight_spills_waiting_room_to_previous_civil_day()
    {
        var opening = new TimeOnly(0, 15);
        var beforeRoom = ReservationQueueHarness.Brazil(23, 40);
        var beforeDate = ReservationQueueClock.ResolveOpeningDate(beforeRoom, opening);
        Assert.Equal(ReservationQueueHarness.OpeningDate, beforeDate);
        Assert.Equal(
            QueuePhase.Open,
            ReservationQueueClock.ResolvePhase(
                beforeRoom,
                ReservationQueueClock.WaitingRoomOpensAt(beforeDate, opening),
                ReservationQueueClock.OpensAt(beforeDate, opening)));

        var inRoom = ReservationQueueHarness.Brazil(23, 50);
        var inRoomDate = ReservationQueueClock.ResolveOpeningDate(inRoom, opening);
        Assert.Equal(ReservationQueueHarness.OpeningDate.AddDays(1), inRoomDate);
        Assert.Equal(
            QueuePhase.WaitingRoom,
            ReservationQueueClock.ResolvePhase(
                inRoom,
                ReservationQueueClock.WaitingRoomOpensAt(inRoomDate, opening),
                ReservationQueueClock.OpensAt(inRoomDate, opening)));

        var nextDay = ReservationQueueHarness.OpeningDate.AddDays(1);
        var afterOpen = ReservationQueueHarness.Brazil(nextDay, 0, 16);
        var afterDate = ReservationQueueClock.ResolveOpeningDate(afterOpen, opening);
        Assert.Equal(nextDay, afterDate);
        Assert.Equal(
            QueuePhase.Open,
            ReservationQueueClock.ResolvePhase(
                afterOpen,
                ReservationQueueClock.WaitingRoomOpensAt(afterDate, opening),
                ReservationQueueClock.OpensAt(afterDate, opening)));
    }
}
