using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authentication;

public sealed class PublicTenantBinder(
    AppDbContext dbContext,
    AmbientTenantContext ambientTenantContext) : IPublicTenantBinder
{
    public async Task BindFromSubdomainAsync(
        string? subdomain,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subdomain))
        {
            throw new TenantResolutionException(
                "Tenant subdomain is required (header X-Tenant-Subdomain).");
        }

        var normalized = subdomain.Trim().ToLowerInvariant();

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.Subdomain != null
                     && t.Subdomain.ToLower() == normalized
                     && t.IsActive,
                cancellationToken);

        if (tenant is null)
        {
            throw new TenantResolutionException(
                $"No active tenant found for subdomain '{normalized}'.");
        }

        ambientTenantContext.TenantId = tenant.Id;
    }
}
