using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed record EffectiveBookingPolicy(
    int? MaxAdvanceBookingDays,
    int? CancelCutoffHours,
    int? MaxActiveReservationsPerCustomerPerDay,
    string? CancellationPolicyText,
    int? MinimumBookingLeadMinutes = null,
    int? DepositPercent = null);

/// <summary>Resource overrides, then category policies, then tenant defaults, then unlimited defaults.</summary>
public sealed class PolicyResolver(AppDbContext db)
{
    public Task<BookingPolicy?> ReadTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.BookingPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.ResourceType == null && p.AssetCategoryId == null, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, BookingPolicy>> ReadCategoriesAsync(
        Guid tenantId, CancellationToken cancellationToken) =>
        await db.BookingPolicies.AsNoTracking()
            .Where(p => p.TenantId == tenantId && p.AssetCategoryId != null && p.ResourceType == null)
            .ToDictionaryAsync(p => p.AssetCategoryId!.Value, cancellationToken);

    public static int? StrictestCancelCutoffHours(IEnumerable<RentalAsset> resources,
        IReadOnlyDictionary<Guid, BookingPolicy> categories, BookingPolicy? tenant) =>
        resources.Select(resource => Resolve(resource,
            categories.GetValueOrDefault(resource.Asset.CategoryId), tenant).CancelCutoffHours).Max();

    public static DateTimeOffset? CancelDeadlineUtc(
        DateTimeOffset start, IEnumerable<RentalAsset> resources,
        IReadOnlyDictionary<Guid, BookingPolicy> categories, BookingPolicy? tenant)
    {
        var cutoff = StrictestCancelCutoffHours(resources, categories, tenant);
        return cutoff is > 0 ? start.AddHours(-cutoff.Value).ToUniversalTime() : null;
    }

    public static int? ValidateCancelCutoffHours(int? hours)
    {
        if (hours is < 0 or > 8760)
            throw new ArgumentException("Cancellation cutoff must be an integer from 0 to 8760, or null for no cutoff.");
        return hours;
    }

    public static int? EffectiveLeadMinutes(int? resourceMinutes, BookingPolicy? category, BookingPolicy? tenant) =>
        resourceMinutes ?? category?.MinimumBookingLeadMinutes ?? tenant?.MinimumBookingLeadMinutes;

    public static int? EffectiveDepositPercent(int? resourcePercent, BookingPolicy? category, BookingPolicy? tenant) =>
        resourcePercent ?? category?.DepositPercent ?? tenant?.DepositPercent;

    public static EffectiveBookingPolicy Resolve(RentalAsset? resource, BookingPolicy? category, BookingPolicy? tenant) =>
        new(resource?.MaxAdvanceBookingDays ?? category?.MaxAdvanceBookingDays ?? tenant?.MaxAdvanceBookingDays,
            resource?.CancelCutoffHours ?? category?.CancelCutoffHours ?? tenant?.CancelCutoffHours,
            tenant?.MaxActiveReservationsPerCustomerPerDay,
            tenant?.CancellationPolicyText,
            EffectiveLeadMinutes(resource?.MinimumBookingLeadMinutes, category, tenant),
            EffectiveDepositPercent(resource?.DepositPercent, category, tenant));
}
