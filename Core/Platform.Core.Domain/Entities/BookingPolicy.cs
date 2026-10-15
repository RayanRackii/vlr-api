using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Tenant booking policy for customer self-booking. The row with
/// <see cref="ResourceType"/> null is the default and applies to every
/// bookable resource type. A non-null <see cref="ResourceType"/> is reserved
/// for a later per-type override; the current API does not read or write it.
/// </summary>
public class BookingPolicy : Entity, ITenantScoped
{
    public const int CancellationPolicyMaxLength = 2000;

    public const int MinDailyActiveReservations = 1;

    public const int MaxDailyActiveReservations = 100;

    public required Guid TenantId { get; set; }

    /// <summary>
    /// Null applies this policy to every bookable resource type.
    /// </summary>
    public RentalAssetType? ResourceType { get; set; }

    /// <summary>Plain-text cancellation note. No refund calculation.</summary>
    public string? CancellationPolicyText { get; set; }

    /// <summary>
    /// Cap on a customer's <c>PendingDeposit</c> and <c>Confirmed</c> reservations
    /// whose start falls on one civil day. <c>Completed</c> and <c>Canceled</c> do
    /// not count. Null means unlimited. The day uses the tenant timezone when the
    /// tenant model has one; otherwise America/Sao_Paulo.
    /// </summary>
    public int? MaxActiveReservationsPerCustomerPerDay { get; set; }

    public void Replace(string? cancellationPolicyText, int? maxActiveReservationsPerCustomerPerDay)
    {
        CancellationPolicyText = cancellationPolicyText;
        MaxActiveReservationsPerCustomerPerDay = maxActiveReservationsPerCustomerPerDay;
        MarkAsUpdated();
    }
}
