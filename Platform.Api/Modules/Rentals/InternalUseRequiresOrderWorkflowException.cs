namespace Platform.Api.Modules.Rentals;

/// <summary>
/// A staff Rentals mutation targeted an <c>InternalUse</c> reservation directly. InternalUse
/// reservations are owned by a Catalog order's allocation lifecycle (Scheduled -&gt; Running -&gt;
/// Cleaning -&gt; Released) and must only be mutated through that order's allocation-usage workflow,
/// never through the generic Rentals reservation/schedule endpoints.
/// </summary>
public sealed class InternalUseRequiresOrderWorkflowException(Guid reservationId)
    : InvalidOperationException(
        $"Reservation '{reservationId}' is an internal-use resource occupancy owned by a catalog " +
        "order. Use the owning catalog order's allocation-usage workflow instead of mutating this " +
        "reservation directly.")
{
    public const string Code = "internal_use_requires_order_workflow";

    public Guid ReservationId { get; } = reservationId;
}
