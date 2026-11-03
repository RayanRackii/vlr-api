using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Implements <see cref="IResourceOccupancyService"/> on top of the existing
/// <see cref="RentalAssetLocks"/> advisory-lock helper and the same half-open overlap query used by
/// <see cref="ReservationService"/>/<see cref="ScheduleService"/> (<c>start &lt; otherEnd &amp;&amp;
/// end &gt; otherStart</c>), filtered to <see cref="AvailabilityReadModel.BlockingStatuses"/>.
/// CustomerBooking and InternalUse reservations block identically by design (see Order-Resource
/// Planning spec §3), with one exception: an overdue, unreleased (Confirmed, not yet Completed)
/// InternalUse reservation blocks past its planned EndDateTime — "now" is never an implicit
/// release — until <see cref="ReleaseInternalUseOccupancyAsync"/> explicitly closes it. This
/// service is stateless
/// (no injected AppDbContext of its own) so it can be called against whichever AppDbContext/
/// transaction the caller (e.g. Catalog's approval flow) is already inside.
/// </summary>
public sealed class ResourceOccupancyService(TimeProvider? timeProvider = null) : IResourceOccupancyService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task LockResourcesAsync(
        AppDbContext db,
        Guid tenantId,
        IEnumerable<Guid> rentalAssetIds,
        CancellationToken cancellationToken) =>
        RentalAssetLocks.LockByRentalAssetIdsAsync(db, tenantId, rentalAssetIds, cancellationToken);

    public async Task<ResourceAvailabilityResult> CheckAvailabilityAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        DateTimeOffset occupiedStart,
        DateTimeOffset occupiedEnd,
        IReadOnlyCollection<TentativeOccupancy> tentativeBatch,
        CancellationToken cancellationToken)
    {
        var conflict = await FindPersistedConflictAsync(
            db, tenantId, rentalAssetId, occupiedStart, occupiedEnd, excludeReservationId: null, cancellationToken);
        if (conflict is { } persisted)
        {
            return new ResourceAvailabilityResult(
                IsAvailable: false,
                ConflictReservationId: persisted,
                ConflictSource: ResourceOccupancyConflictSource.PersistedReservation,
                Reason: $"Resource '{rentalAssetId}' is already occupied for an overlapping interval.");
        }

        foreach (var tentative in tentativeBatch)
        {
            if (tentative.RentalAssetId != rentalAssetId)
            {
                continue;
            }

            if (tentative.Start < occupiedEnd && tentative.End > occupiedStart)
            {
                return new ResourceAvailabilityResult(
                    IsAvailable: false,
                    ConflictReservationId: null,
                    ConflictSource: ResourceOccupancyConflictSource.TentativeBatch,
                    Reason: $"Resource '{rentalAssetId}' is already claimed by another line in this batch.");
            }
        }

        return new ResourceAvailabilityResult(true, null, null, null);
    }

    public async Task<Guid> CreateInternalUseOccupancyAsync(
        AppDbContext db,
        Guid tenantId,
        Guid unitId,
        Guid rentalAssetId,
        int quantity,
        DateTimeOffset occupiedStart,
        DateTimeOffset occupiedEnd,
        CancellationToken cancellationToken)
    {
        if (occupiedEnd <= occupiedStart)
        {
            throw new ArgumentException("occupiedEnd must be greater than occupiedStart.");
        }

        if (quantity < 1)
        {
            throw new ArgumentException("quantity must be at least 1.");
        }

        // Defense in depth: the caller is expected to already hold the resource lock (ascending
        // order, via LockResourcesAsync) before calling this method, but we revalidate under that
        // lock rather than trust the caller blindly.
        var conflict = await FindPersistedConflictAsync(
            db, tenantId, rentalAssetId, occupiedStart, occupiedEnd, excludeReservationId: null, cancellationToken);
        if (conflict is { } conflictingReservationId)
        {
            throw new ResourceUnavailableException(
                rentalAssetId,
                $"Resource '{rentalAssetId}' conflicts with reservation '{conflictingReservationId}'.");
        }

        var rentalAsset = await db.RentalAssets
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(r => r.Id == rentalAssetId && r.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Rental asset '{rentalAssetId}' was not found.");

        // The caller-supplied unitId must resolve inside the current tenant (global query filter
        // on Unit) and must match the resource's own unit — otherwise a planning request could
        // stamp an InternalUse reservation with a foreign or mismatched unit id, which the FK alone
        // (tenant-qualified, see ReservationConfiguration) can only reject at a different layer.
        var unit = await db.Units
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == unitId && u.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Unit '{unitId}' was not found.");

        if (rentalAsset.Asset.UnitId != unit.Id)
        {
            throw new KeyNotFoundException("Resource not found for this unit.");
        }

        // InternalUse never needs a deposit and is constructed already Confirmed — it must never
        // go through ReservationService.CreateReservationAsync's customer pricing/deposit pipeline
        // or its PendingDeposit opening state.
        var reservation = new Reservation
        {
            TenantId = tenantId,
            UnitId = unitId,
            Purpose = ReservationPurpose.InternalUse,
            CustomerId = null,
            CustomerName = null,
            CustomerWhatsApp = null,
            StartDateTime = occupiedStart,
            EndDateTime = occupiedEnd,
            Status = ReservationStatus.Confirmed,
            TotalAmount = 0m,
            DepositPaid = 0m,
        };

        var item = new ReservationItem
        {
            TenantId = tenantId,
            ReservationId = reservation.Id,
            RentalAssetId = rentalAsset.Id,
            Quantity = quantity,
            UnitPrice = 0m,
            SubTotal = 0m,
        };

        reservation.AddItem(item);

        db.Reservations.Add(reservation);
        db.ReservationItems.Add(item);

        return item.Id;
    }

    public async Task ReleaseInternalUseOccupancyAsync(
        AppDbContext db,
        Guid tenantId,
        Guid reservationItemId,
        InternalUseReleaseKind releaseKind,
        DateTimeOffset plannedWorkEnd,
        CancellationToken cancellationToken)
    {
        var item = await db.ReservationItems
            .Include(i => i.Reservation)
            .FirstOrDefaultAsync(
                i => i.Id == reservationItemId && i.TenantId == tenantId,
                cancellationToken)
            ?? throw new KeyNotFoundException($"Reservation item '{reservationItemId}' was not found.");

        var reservation = item.Reservation;
        if (reservation.Purpose != ReservationPurpose.InternalUse)
        {
            throw new InvalidOperationException(
                $"Reservation '{reservation.Id}' is not an InternalUse reservation.");
        }

        // Idempotent: both Completed and Canceled are terminal, non-open states. A retry (e.g. a
        // lost response) must neither re-extend nor re-shorten an already-closed window.
        if (reservation.Status is ReservationStatus.Completed or ReservationStatus.Canceled)
        {
            return;
        }

        if (releaseKind == InternalUseReleaseKind.CancelBeforeStart)
        {
            // The work never started, so nothing physical happened in this window: free it
            // entirely — prep, work and cleanup buffers alike. Canceled is the existing
            // non-blocking terminal status (absent from AvailabilityReadModel.BlockingStatuses),
            // unlike Completed which keeps blocking its window, so the prep/cleanup buffers become
            // immediately bookable again and the staff agenda (same BlockingStatuses) drops the
            // cell.
            reservation.Status = ReservationStatus.Canceled;
            reservation.Touch();
            return;
        }

        // ReleaseAfterStart: the resource is physically free from the release moment onward except
        // for the cleanup allowance that still has to run, so the occupied window closes at
        // releaseTime + cleanupBuffer. Design choice:
        //   * early release (before the planned work end) shortens the window, giving back the
        //     unused tail between (release + cleanup) and the originally planned end;
        //   * release at/after the planned end leaves the window unchanged — releaseTime + cleanup
        //     is already >= the planned end — and, for an overdue reservation, extends it to the
        //     actual release time so the overrun is preserved in history (times after it are free).
        // The cleanup buffer is recovered from the persisted window (occupiedEnd == plannedWorkEnd
        // + cleanup), keeping this method agnostic to how the buffer was originally configured.
        var releasedAt = clock.GetUtcNow();
        var cleanupBuffer = reservation.EndDateTime - plannedWorkEnd;
        if (cleanupBuffer < TimeSpan.Zero)
        {
            cleanupBuffer = TimeSpan.Zero;
        }

        var shortenedEnd = releasedAt + cleanupBuffer;
        if (shortenedEnd < reservation.EndDateTime && shortenedEnd > reservation.StartDateTime)
        {
            reservation.EndDateTime = shortenedEnd;
        }
        else if (releasedAt > reservation.EndDateTime)
        {
            reservation.EndDateTime = releasedAt;
        }

        reservation.Status = ReservationStatus.Completed;
        reservation.Touch();
    }

    public async Task<EligibleResource?> ResolveEligibleResourceAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken)
    {
        var rentalAsset = await db.RentalAssets
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(r => r.Id == rentalAssetId && r.TenantId == tenantId, cancellationToken);
        if (rentalAsset is null)
        {
            return null;
        }

        var ineligible = !rentalAsset.IsActive
            || rentalAsset.Asset.Status != AssetStatus.Active
            || rentalAsset.Asset.ScheduledDeletionAt is not null;

        return new EligibleResource(
            rentalAsset.Id,
            rentalAsset.Asset.Name,
            IsEligible: !ineligible,
            IneligibilityReason: ineligible
                ? "The configured resource is not currently available for production use."
                : null);
    }

    public async Task<WorkWindowValidation> ValidateWorkWindowAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        DateTimeOffset occupiedStart,
        DateTimeOffset occupiedEnd,
        CancellationToken cancellationToken)
    {
        var rentalAsset = await db.RentalAssets
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(r => r.Id == rentalAssetId && r.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Rental asset '{rentalAssetId}' was not found.");

        var zone = await TenantBookingClock.ResolveForTenantAsync(db, tenantId, cancellationToken);
        var zoneId = await TenantBookingClock.ResolveIanaIdForTenantAsync(db, tenantId, cancellationToken);
        var error = await ResourceWorkWindowValidator.ValidateAsync(
            db, tenantId, rentalAsset, occupiedStart, occupiedEnd, zone, rentalAsset.Asset.Name, cancellationToken);

        return new WorkWindowValidation(zone, zoneId, error);
    }

    /// <summary>
    /// Half-open overlap against every blocking reservation (CustomerBooking and InternalUse
    /// alike — no Purpose filter), mirroring <see cref="ReservationService.GetReservedQuantityAsync"/>
    /// and <see cref="ScheduleService.GetReservedQuantityAsync"/>. Returns the first conflicting
    /// reservation id, if any; exclusive resources (expected quantity 1 in Phase 0) only ever have
    /// zero or one conflict, but this still reports just one id even when more exist.
    /// </summary>
    private async Task<Guid?> FindPersistedConflictAsync(
        AppDbContext db,
        Guid tenantId,
        Guid rentalAssetId,
        DateTimeOffset start,
        DateTimeOffset end,
        Guid? excludeReservationId,
        CancellationToken cancellationToken)
    {
        var query =
            from item in db.ReservationItems.AsNoTracking()
            join reservation in ReservationOccupancy.BlockingWindows(
                db.Reservations.AsNoTracking(), clock.GetUtcNow())
                on item.ReservationId equals reservation.Id
            where item.TenantId == tenantId
                  && reservation.TenantId == tenantId
                  && item.RentalAssetId == rentalAssetId
                  && reservation.StartDateTime < end
                  && reservation.EndDateTime > start
            select reservation.Id;

        if (excludeReservationId is Guid excludedId)
        {
            query = query.Where(id => id != excludedId);
        }

        return await query.FirstOrDefaultAsync(cancellationToken) is { } id && id != Guid.Empty
            ? id
            : null;
    }
}
