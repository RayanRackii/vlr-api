using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Suggested horizon for a tenant that does not exist yet. Existing tenants
/// keep a null horizon (unlimited) until an admin sets one.
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
