using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Horizon stored for a tenant at provisioning. Call this only while creating
/// the tenant. A later read, and the first save for a tenant that has no row,
/// leave the horizon unlimited unless the request sets one.
/// </summary>
internal static class BookingPolicyDefaults
{
    public static BookingPolicy SuggestedFor(Guid tenantId) =>
        new()
        {
            TenantId = tenantId,
            ResourceType = null,
            MaxAdvanceBookingDays = BookingPolicy.SuggestedMaxAdvanceBookingDays,
        };
}
