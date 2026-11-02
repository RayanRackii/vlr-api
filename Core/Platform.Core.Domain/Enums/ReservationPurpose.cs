namespace Platform.Core.Domain.Enums;

/// <summary>
/// Distinguishes a customer-facing booking from an internal (operational) resource use
/// created by order-resource planning. InternalUse reservations have no customer identity
/// and no financial amounts.
/// </summary>
public enum ReservationPurpose
{
    CustomerBooking = 0,
    InternalUse = 1,
}
