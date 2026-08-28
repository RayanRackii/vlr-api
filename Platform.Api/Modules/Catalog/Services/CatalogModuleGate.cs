using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Exceptions;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogModuleGate
{
    Task EnsureActiveAsync(CancellationToken cancellationToken = default);
}

public sealed class CatalogModuleGate(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : ICatalogModuleGate
{
    public async Task EnsureActiveAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var active = await dbContext.TenantModules
            .AsNoTracking()
            .AnyAsync(
                module => module.ModuleName == PlatformModules.Catalog && module.IsActive,
                cancellationToken);

        if (!active)
        {
            throw new CatalogModuleInactiveException();
        }
    }
}
