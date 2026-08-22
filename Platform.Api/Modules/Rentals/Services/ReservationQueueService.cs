using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class ReservationQueueService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    TimeProvider timeProvider,
    ILogger<ReservationQueueService> logger) : IReservationQueueService
{
    public Task<ReservationQueueStatusDto> GetStatusAsync(
        Guid rentalAssetId,
        Guid customerId,
        CancellationToken cancellationToken) =>
        MutateAsync(rentalAssetId, customerId, JoinMode.None, cancellationToken);

    public Task<ReservationQueueStatusDto> JoinAsync(
        Guid rentalAssetId,
        Guid customerId,
        CancellationToken cancellationToken) =>
        MutateAsync(rentalAssetId, customerId, JoinMode.Join, cancellationToken);

    public Task<ReservationQueueStatusDto> LeaveAsync(
        Guid rentalAssetId,
        Guid customerId,
        CancellationToken cancellationToken) =>
        MutateAsync(rentalAssetId, customerId, JoinMode.Leave, cancellationToken);

    public async Task EnsureActiveTurnForBookingAsync(
        Guid customerId,
        RentalAsset rentalAsset,
        CancellationToken cancellationToken)
    {
        if (!RequiresQueue(rentalAsset))
        {
            return;
        }

        EnsureTenant();
        await RentalAssetLocks.LockByRentalAssetIdAsync(dbContext, rentalAsset.Id, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var session = await EnsureSessionAsync(rentalAsset, now, cancellationToken);
        await AdvanceAsync(session, now, skipExpiringCustomerId: null, cancellationToken);

        ThrowIfBookingNotAllowed(customerId, rentalAsset.Id, session, now);
    }

    public async Task CompleteTurnAsync(
        Guid customerId,
        RentalAsset rentalAsset,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        if (!RequiresQueue(rentalAsset))
        {
            return;
        }

        EnsureTenant();
        await RentalAssetLocks.LockByRentalAssetIdAsync(dbContext, rentalAsset.Id, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var session = await EnsureSessionAsync(rentalAsset, now, cancellationToken);
        await AdvanceAsync(session, now, skipExpiringCustomerId: null, cancellationToken);
        ThrowIfBookingNotAllowed(customerId, rentalAsset.Id, session, now);

        var ticket = session.Tickets.First(t =>
            t.CustomerId == customerId && t.Status == QueueTicketStatus.Active);

        ticket.Complete(reservationId);
        logger.LogInformation(
            "Reservation queue {QueueAction} code={Code} rentalAssetId={RentalAssetId} sessionId={SessionId} customerId={CustomerId} sequence={Sequence}",
            "complete",
            QueueTicketStatus.Completed.ToString(),
            rentalAsset.Id,
            session.Id,
            customerId,
            ticket.Sequence);

        await PromoteAsync(session, now, cancellationToken);
    }

    private async Task<ReservationQueueStatusDto> MutateAsync(
        Guid rentalAssetId,
        Guid customerId,
        JoinMode mode,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var rental = await LoadQueuedLocationAsync(rentalAssetId, cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await RentalAssetLocks.LockByRentalAssetIdAsync(dbContext, rental.Id, cancellationToken);

            var now = timeProvider.GetUtcNow();
            var session = await EnsureSessionAsync(rental, now, cancellationToken);
            await AdvanceAsync(session, now, skipExpiringCustomerId: null, cancellationToken);

            if (mode == JoinMode.Join)
            {
                await JoinUnderLockAsync(rental, session, customerId, now, cancellationToken);
            }
            else if (mode == JoinMode.Leave)
            {
                await LeaveUnderLockAsync(rental, session, customerId, now, cancellationToken);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) when (mode == JoinMode.Join)
            {
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return await RecoverIdempotentJoinAsync(rental, customerId, now, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return ToStatus(rental, session, customerId, now);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    private async Task<ReservationQueueStatusDto> RecoverIdempotentJoinAsync(
        RentalAsset rental,
        Guid customerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var openingTime = rental.QueueOpeningTime
            ?? throw new ArgumentException(
                "QueueOpeningTime is required when the waiting queue is enabled.");
        var openingDate = ReservationQueueClock.ResolveOpeningDate(now, openingTime);
        var session = await dbContext.ReservationQueueSessions
            .AsNoTracking()
            .Include(s => s.Tickets)
            .FirstOrDefaultAsync(
                s => s.RentalAssetId == rental.Id && s.OpeningDate == openingDate,
                cancellationToken)
            ?? throw new InvalidOperationException(ReservationQueueCodes.Required);

        if (FindLiveTicket(session, customerId) is null)
        {
            throw new InvalidOperationException(ReservationQueueCodes.Required);
        }

        return ToStatus(rental, session, customerId, now);
    }

    private async Task JoinUnderLockAsync(
        RentalAsset rental,
        ReservationQueueSession session,
        Guid customerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var phase = ReservationQueueClock.ResolvePhase(
            now,
            session.WaitingRoomOpensAt,
            session.OpensAt);

        if (phase == QueuePhase.Closed)
        {
            logger.LogInformation(
                "Reservation queue {QueueAction} code={Code} rentalAssetId={RentalAssetId} sessionId={SessionId} customerId={CustomerId} sequence={Sequence}",
                "reject",
                ReservationQueueCodes.WaitingRoomClosed,
                rental.Id,
                session.Id,
                customerId,
                (long?)null);
            throw new ArgumentException(ReservationQueueCodes.WaitingRoomClosed);
        }

        var live = FindLiveTicket(session, customerId);
        if (live is not null)
        {
            logger.LogInformation(
                "Reservation queue {QueueAction} code={Code} rentalAssetId={RentalAssetId} sessionId={SessionId} customerId={CustomerId} sequence={Sequence}",
                "join",
                live.Status.ToString(),
                rental.Id,
                session.Id,
                customerId,
                live.Sequence);
            return;
        }

        var isRejoin = session.Tickets.Any(t => t.CustomerId == customerId);
        var sequence = NextSequence(session);
        var ticket = new ReservationQueueTicket
        {
            TenantId = session.TenantId,
            QueueSessionId = session.Id,
            CustomerId = customerId,
            Sequence = sequence,
            Status = QueueTicketStatus.Waiting,
            JoinedAt = now,
        };
        session.AddTicket(ticket);
        dbContext.ReservationQueueTickets.Add(ticket);

        logger.LogInformation(
            "Reservation queue {QueueAction} code={Code} rentalAssetId={RentalAssetId} sessionId={SessionId} customerId={CustomerId} sequence={Sequence}",
            isRejoin ? "rejoin" : "join",
            QueueTicketStatus.Waiting.ToString(),
            rental.Id,
            session.Id,
            customerId,
            sequence);

        if (now >= session.OpensAt && !HasActive(session))
        {
            Activate(rental.Id, session, ticket, now);
        }

        await Task.CompletedTask;
        _ = cancellationToken;
    }

    private async Task LeaveUnderLockAsync(
        RentalAsset rental,
        ReservationQueueSession session,
        Guid customerId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var live = FindLiveTicket(session, customerId);
        if (live is null)
        {
            return;
        }

        var wasActive = live.Status == QueueTicketStatus.Active;
        live.Cancel();
        logger.LogInformation(
            "Reservation queue {QueueAction} code={Code} rentalAssetId={RentalAssetId} sessionId={SessionId} customerId={CustomerId} sequence={Sequence}",
            "leave",
            QueueTicketStatus.Cancelled.ToString(),
            rental.Id,
            session.Id,
            customerId,
            live.Sequence);

        if (wasActive)
        {
            await PromoteAsync(session, now, cancellationToken);
        }
    }

    private async Task<ReservationQueueSession> EnsureSessionAsync(
        RentalAsset rental,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var openingTime = rental.QueueOpeningTime
            ?? throw new ArgumentException("QueueOpeningTime is required when the waiting queue is enabled.");
        var openingDate = ReservationQueueClock.ResolveOpeningDate(now, openingTime);

        var existing = await dbContext.ReservationQueueSessions
            .Include(s => s.Tickets)
            .FirstOrDefaultAsync(
                s => s.RentalAssetId == rental.Id && s.OpeningDate == openingDate,
                cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var session = new ReservationQueueSession
        {
            TenantId = rental.TenantId,
            RentalAssetId = rental.Id,
            OpeningDate = openingDate,
            OpensAt = ReservationQueueClock.OpensAt(openingDate, openingTime),
            WaitingRoomOpensAt = ReservationQueueClock.WaitingRoomOpensAt(openingDate, openingTime),
        };
        dbContext.ReservationQueueSessions.Add(session);
        return session;
    }

    private async Task AdvanceAsync(
        ReservationQueueSession session,
        DateTimeOffset now,
        Guid? skipExpiringCustomerId,
        CancellationToken cancellationToken)
    {
        foreach (var ticket in session.Tickets
                     .Where(t => t.Status == QueueTicketStatus.Active)
                     .ToList())
        {
            if (skipExpiringCustomerId is { } skip && ticket.CustomerId == skip)
            {
                continue;
            }

            if (ticket.TurnExpiresAt is { } expiresAt && expiresAt <= now)
            {
                ticket.Expire();
                logger.LogInformation(
                    "Reservation queue {QueueAction} code={Code} rentalAssetId={RentalAssetId} sessionId={SessionId} customerId={CustomerId} sequence={Sequence}",
                    "expire",
                    ReservationQueueCodes.TurnExpired,
                    session.RentalAssetId,
                    session.Id,
                    ticket.CustomerId,
                    ticket.Sequence);
            }
        }

        await PromoteAsync(session, now, cancellationToken);
    }

    private Task PromoteAsync(
        ReservationQueueSession session,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (now < session.OpensAt || HasActive(session))
        {
            return Task.CompletedTask;
        }

        var next = session.Tickets
            .Where(t => t.Status == QueueTicketStatus.Waiting)
            .OrderBy(t => t.Sequence)
            .FirstOrDefault();

        if (next is not null)
        {
            Activate(session.RentalAssetId, session, next, now);
        }

        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private void Activate(
        Guid rentalAssetId,
        ReservationQueueSession session,
        ReservationQueueTicket ticket,
        DateTimeOffset now)
    {
        ticket.Activate(now, ReservationQueueClock.TurnDuration);
        logger.LogInformation(
            "Reservation queue {QueueAction} code={Code} rentalAssetId={RentalAssetId} sessionId={SessionId} customerId={CustomerId} sequence={Sequence}",
            "activate",
            QueueTicketStatus.Active.ToString(),
            rentalAssetId,
            session.Id,
            ticket.CustomerId,
            ticket.Sequence);
    }

    private void ThrowIfBookingNotAllowed(
        Guid customerId,
        Guid rentalAssetId,
        ReservationQueueSession session,
        DateTimeOffset now)
    {
        var live = FindLiveTicket(session, customerId);
        if (live is not null
            && live.Status == QueueTicketStatus.Active
            && live.TurnExpiresAt is { } expiresAt
            && expiresAt > now)
        {
            return;
        }

        string code;
        long? sequence = live?.Sequence;

        if (live is { Status: QueueTicketStatus.Waiting })
        {
            code = ReservationQueueCodes.Waiting;
        }
        else if (session.Tickets.Any(t =>
                     t.CustomerId == customerId && t.Status == QueueTicketStatus.Completed))
        {
            code = ReservationQueueCodes.TurnAlreadyUsed;
            sequence = session.Tickets
                .Where(t => t.CustomerId == customerId && t.Status == QueueTicketStatus.Completed)
                .Select(t => t.Sequence)
                .Max();
        }
        else if (live is { Status: QueueTicketStatus.Active }
                 || session.Tickets.Any(t =>
                     t.CustomerId == customerId && t.Status == QueueTicketStatus.Expired))
        {
            code = ReservationQueueCodes.TurnExpired;
            sequence ??= session.Tickets
                .Where(t => t.CustomerId == customerId && t.Status == QueueTicketStatus.Expired)
                .Select(t => t.Sequence)
                .DefaultIfEmpty()
                .Max();
        }
        else
        {
            code = ReservationQueueCodes.Required;
        }

        logger.LogInformation(
            "Reservation queue {QueueAction} code={Code} rentalAssetId={RentalAssetId} sessionId={SessionId} customerId={CustomerId} sequence={Sequence}",
            "reject",
            code,
            rentalAssetId,
            session.Id,
            customerId,
            sequence);
        throw new InvalidOperationException(code);
    }

    private async Task<RentalAsset> LoadQueuedLocationAsync(
        Guid rentalAssetId,
        CancellationToken cancellationToken)
    {
        var rental = await dbContext.RentalAssets
            .FirstOrDefaultAsync(r => r.Id == rentalAssetId && r.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("Rentable was not found.");

        if (rental.Type != RentalAssetType.Location || !rental.QueueEnabled)
        {
            throw new KeyNotFoundException("Waiting queue is not enabled for this rentable.");
        }

        return rental;
    }

    private Guid EnsureTenant()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static bool RequiresQueue(RentalAsset rental) =>
        rental.Type == RentalAssetType.Location && rental.QueueEnabled;

    private static ReservationQueueTicket? FindLiveTicket(
        ReservationQueueSession session,
        Guid customerId) =>
        session.Tickets.FirstOrDefault(t =>
            t.CustomerId == customerId
            && (t.Status == QueueTicketStatus.Waiting || t.Status == QueueTicketStatus.Active));

    private static bool HasActive(ReservationQueueSession session) =>
        session.Tickets.Any(t => t.Status == QueueTicketStatus.Active);

    private static long NextSequence(ReservationQueueSession session) =>
        session.Tickets.Count == 0 ? 1 : session.Tickets.Max(t => t.Sequence) + 1;

    private static ReservationQueueStatusDto ToStatus(
        RentalAsset rental,
        ReservationQueueSession session,
        Guid customerId,
        DateTimeOffset now)
    {
        var waiting = session.Tickets
            .Where(t => t.Status == QueueTicketStatus.Waiting)
            .OrderBy(t => t.Sequence)
            .ToList();
        var live = FindLiveTicket(session, customerId);
        var mine = live
            ?? session.Tickets
                .Where(t => t.CustomerId == customerId)
                .OrderByDescending(t => t.Sequence)
                .FirstOrDefault();
        var waitingCount = waiting.Count;
        var aheadCount = live is { Status: QueueTicketStatus.Waiting }
            ? waiting.Count(t => t.Sequence < live.Sequence)
            : live is { Status: QueueTicketStatus.Active }
                ? 0
                : waitingCount;
        var position = live is { Status: QueueTicketStatus.Waiting }
            ? aheadCount + 1
            : live is { Status: QueueTicketStatus.Active }
                ? 1
                : 0;

        ReservationQueueTicketDto? myTicket = mine is null
            ? null
            : new ReservationQueueTicketDto(
                mine.Id,
                mine.Status,
                mine.Sequence,
                position,
                mine.JoinedAt,
                mine.TurnStartedAt,
                mine.TurnExpiresAt,
                mine.CompletedReservationId);

        return new ReservationQueueStatusDto(
            rental.Id,
            rental.QueueEnabled,
            session.OpeningDate,
            session.OpensAt,
            session.WaitingRoomOpensAt,
            now,
            ReservationQueueClock.ResolvePhase(now, session.WaitingRoomOpensAt, session.OpensAt),
            waitingCount,
            aheadCount,
            myTicket);
    }

    private enum JoinMode
    {
        None,
        Join,
        Leave
    }
}
