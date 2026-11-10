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

    /// <summary>
    /// Written only when a tenant is provisioned. A missing policy row and an
    /// explicit null stay unlimited. Reads never apply this value.
    /// </summary>
    public const int SuggestedMaxAdvanceBookingDays = 7;

    public const int MinAdvanceBookingDays = 0;

    public const int MaxAdvanceBookingDaysUpperBound = 3650;

    public const int MinBookingLeadMinutes = 0;

    /// <summary>365 days. Longer than that is the advance horizon, not a lead time.</summary>
    public const int MaxBookingLeadMinutes = 365 * 24 * 60;

    public const int MinDepositPercent = 0;

    public const int MaxDepositPercent = 100;

    public required Guid TenantId { get; set; }

    /// <summary>
    /// Null applies this policy to every bookable resource type.
    /// </summary>
    public RentalAssetType? ResourceType { get; set; }

    /// <summary>Tenant-owned category override; null for tenant defaults and reserved type policies.</summary>
    public Guid? AssetCategoryId { get; set; }

    /// <summary>Plain-text cancellation note. No refund calculation.</summary>
    public string? CancellationPolicyText { get; set; }

    /// <summary>
    /// Cap on a customer's <c>PendingDeposit</c> and <c>Confirmed</c> reservations
    /// whose start falls on one civil day. <c>Completed</c> and <c>Canceled</c> do
    /// not count. Null means unlimited. The day uses the tenant timezone; an unset or invalid zone defaults to America/Sao_Paulo.
    /// </summary>
    public int? MaxActiveReservationsPerCustomerPerDay { get; set; }

    /// <summary>
    /// Maximum civil days between today and the booking start, in the tenant
    /// timezone. Null means unlimited. A per-resource value on
    /// <see cref="RentalAsset.MaxAdvanceBookingDays"/> wins when it is set.
    /// </summary>
    public int? MaxAdvanceBookingDays { get; set; }

    /// <summary>Customer cancellation cutoff in hours; null means no cutoff.</summary>
    public int? CancelCutoffHours { get; set; }

    /// <summary>
    /// How many minutes before the start a customer booking must be made.
    /// Null means no minimum. A resource value on
    /// <see cref="RentalAsset.MinimumBookingLeadMinutes"/> wins when it is set,
    /// otherwise the category policy, otherwise this tenant default.
    /// Staff occupancy does not read it.
    /// </summary>
    public int? MinimumBookingLeadMinutes { get; set; }

    /// <summary>
    /// Optional deposit percent from 0 to 100 for setup and display.
    /// Null means no deposit percent is configured. This value is not applied
    /// to a reservation total or to any charge. The pending-deposit email still
    /// uses the pricing window's <see cref="RentalPricing.DepositPercentage"/>.
    /// A resource value on <see cref="RentalAsset.DepositPercent"/> wins when set.
    /// </summary>
    public int? DepositPercent { get; set; }

    public static int? NormalizeLeadMinutes(int? minutes)
    {
        if (minutes is < MinBookingLeadMinutes or > MaxBookingLeadMinutes)
        {
            throw new ArgumentException(
                $"Minimum booking lead time must be an integer from {MinBookingLeadMinutes} to {MaxBookingLeadMinutes}, or null for no minimum.");
        }

        return minutes;
    }

    public static int? NormalizeDepositPercent(int? percent)
    {
        if (percent is < MinDepositPercent or > MaxDepositPercent)
        {
            throw new ArgumentException(
                $"Deposit percent must be an integer from {MinDepositPercent} to {MaxDepositPercent}, or null for no deposit.");
        }

        return percent;
    }

    public void Replace(
        string? cancellationPolicyText,
        int? maxActiveReservationsPerCustomerPerDay,
        int? maxAdvanceBookingDays)
    {
        CancellationPolicyText = cancellationPolicyText;
        MaxActiveReservationsPerCustomerPerDay = maxActiveReservationsPerCustomerPerDay;
        MaxAdvanceBookingDays = maxAdvanceBookingDays;
        MarkAsUpdated();
    }
}
