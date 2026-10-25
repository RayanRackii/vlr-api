using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed record EffectiveBookingPolicy(
    int? MaxAdvanceBookingDays,
    int? CancelCutoffHours,
    int? MaxActiveReservationsPerCustomerPerDay,
    string? CancellationPolicyText);

/// <summary>Resource overrides, then tenant defaults, then unlimited defaults.</summary>
public sealed class PolicyResolver(AppDbContext db)
{
    public Task<BookingPolicy?> ReadTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.BookingPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ResourceType == null, cancellationToken);

    public static int? StrictestCancelCutoffHours(IEnumerable<RentalAsset> resources, BookingPolicy? tenant) =>
        resources.Select(resource => Resolve(resource, tenant).CancelCutoffHours).Max();

    public static DateTimeOffset? CancelDeadlineUtc(
        DateTimeOffset start, IEnumerable<RentalAsset> resources, BookingPolicy? tenant)
    {
        var cutoff = StrictestCancelCutoffHours(resources, tenant);
        return cutoff is > 0 ? start.AddHours(-cutoff.Value).ToUniversalTime() : null;
    }

    public static int? ValidateCancelCutoffHours(int? hours)
    {
        if (hours is < 0 or > 8760)
            throw new ArgumentException("Cancellation cutoff must be an integer from 0 to 8760, or null for no cutoff.");
        return hours;
    }

    public static EffectiveBookingPolicy Resolve(RentalAsset? resource, BookingPolicy? tenant) =>
        new(resource?.MaxAdvanceBookingDays ?? tenant?.MaxAdvanceBookingDays,
            resource?.CancelCutoffHours ?? tenant?.CancelCutoffHours,
            tenant?.MaxActiveReservationsPerCustomerPerDay,
            tenant?.CancellationPolicyText);
}
