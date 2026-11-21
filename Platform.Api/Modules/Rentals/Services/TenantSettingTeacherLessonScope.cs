using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Teacher lessons are allowed only on the rental assets listed in the tenant setting
/// <see cref="TenantSettingKeys.TeacherLessonRentalAssetIds"/>. A missing, empty or malformed
/// setting disables lessons for the tenant. Registered scoped: the setting is read at most once
/// per tenant per request (the instance caches it).
/// </summary>
public sealed class TenantSettingTeacherLessonScope(AppDbContext dbContext) : ITeacherLessonScope
{
    private readonly Dictionary<Guid, HashSet<Guid>> cache = [];

    public async Task<bool> AllowsAsync(
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || rentalAssetId == Guid.Empty)
        {
            return false;
        }

        if (!cache.TryGetValue(tenantId, out var allowed))
        {
            var raw = await TenantSettingStore.ReadValueAsync(
                dbContext, tenantId, TenantSettingKeys.TeacherLessonRentalAssetIds, cancellationToken);
            allowed = TenantSettingValues.TryParseRentalAssetIds(raw, out var ids)
                ? ids.ToHashSet()
                : new HashSet<Guid>();
            cache[tenantId] = allowed;
        }

        return allowed.Contains(rentalAssetId);
    }
}
