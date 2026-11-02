using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>Shared SQL projection for half-open occupancy checks in readers and writers.</summary>
internal sealed class ReservationOccupancy
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public DateTimeOffset StartDateTime { get; init; }
    public DateTimeOffset EndDateTime { get; init; }

    public static IQueryable<ReservationOccupancy> BlockingWindows(
        IQueryable<Reservation> reservations, DateTimeOffset now) =>
        reservations.Where(r => AvailabilityReadModel.BlockingStatuses.Contains(r.Status))
            .Select(r => new ReservationOccupancy
            {
                Id = r.Id,
                TenantId = r.TenantId,
                StartDateTime = r.StartDateTime,
                // Future plans keep their bounded end. Only overdue, unreleased InternalUse
                // extends indefinitely; release persists the actual end before marking Completed.
                EndDateTime = r.Purpose == ReservationPurpose.InternalUse
                    && r.Status == ReservationStatus.Confirmed && r.EndDateTime <= now
                    ? DateTimeOffset.MaxValue : r.EndDateTime,
            });
}
