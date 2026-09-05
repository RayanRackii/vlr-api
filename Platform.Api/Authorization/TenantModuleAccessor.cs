using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authorization;

public sealed class TenantModuleAccessor(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : ITenantModuleAccessor
{
    private static readonly IReadOnlySet<string> Empty =
        new HashSet<string>(StringComparer.Ordinal);

    private Guid? _cachedTenantId;
    private IReadOnlySet<string>? _cached;
    private bool _loaded;

    internal int DatabaseLoadCount { get; private set; }

    public async Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantProvider.TenantId;
        if (_loaded && _cachedTenantId == tenantId && _cached is not null)
        {
            return _cached;
        }

        _cachedTenantId = tenantId;
        _loaded = true;

        if (tenantId is null)
        {
            _cached = Empty;
            return _cached;
        }

        DatabaseLoadCount++;

        var names = await dbContext.TenantModules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(module => module.TenantId == tenantId && module.IsActive)
            .Select(module => module.ModuleName)
            .ToListAsync(cancellationToken);

        _cached = names
            .Select(name => name.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        return _cached;
    }
}
