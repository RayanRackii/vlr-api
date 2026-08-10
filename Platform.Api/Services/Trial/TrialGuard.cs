using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Services.Trial;

public interface ITrialGuard
{
    Task EnsureWritableAsync(CancellationToken cancellationToken);

    Task EnsureCanCreateAssetsAsync(int additionalCount, CancellationToken cancellationToken);

    Task EnsureCanInviteUserAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class TrialGuard(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IPlatformAdminChecker platformAdminChecker) : ITrialGuard
{
    public async Task EnsureWritableAsync(CancellationToken cancellationToken)
    {
        var tenant = await LoadCurrentTrialTenantAsync(cancellationToken);
        if (tenant is null)
        {
            return;
        }

        EnsureNotReadOnly(tenant);
    }

    public async Task EnsureCanCreateAssetsAsync(
        int additionalCount,
        CancellationToken cancellationToken)
    {
        if (additionalCount < 1)
        {
            return;
        }

        var tenant = await LoadCurrentTrialTenantAsync(cancellationToken);
        if (tenant is null)
        {
            return;
        }

        EnsureNotReadOnly(tenant);

        var existingCount = await dbContext.Assets.CountAsync(cancellationToken);
        if (existingCount + additionalCount > TrialLimits.MaxAssets)
        {
            throw new InvalidOperationException(
                $"Trial tenants are limited to {TrialLimits.MaxAssets} assets.");
        }
    }

    public async Task EnsureCanInviteUserAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null || !tenant.IsTrial)
        {
            return;
        }

        EnsureNotReadOnly(tenant);

        var platformEmails = platformAdminChecker.GetNormalizedEmails();

        var userCount = await dbContext.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId)
            .Where(u => platformEmails.Count == 0
                || !platformEmails.Contains(u.Email.ToLower()))
            .CountAsync(cancellationToken);

        var pendingInviteCount = await dbContext.UserInvites
            .IgnoreQueryFilters()
            .Where(i =>
                i.TenantId == tenantId
                && i.AcceptedAt == null
                && i.RevokedAt == null
                && i.ExpiresAt > DateTimeOffset.UtcNow)
            .Where(i => platformEmails.Count == 0
                || !platformEmails.Contains(i.Email.ToLower()))
            .CountAsync(cancellationToken);

        if (userCount + pendingInviteCount >= TrialLimits.MaxUsers)
        {
            throw new InvalidOperationException(
                $"Trial tenants are limited to {TrialLimits.MaxUsers} users.");
        }
    }

    private async Task<Tenant?> LoadCurrentTrialTenantAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId;
        if (tenantId is null)
        {
            return null;
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null || !tenant.IsTrial)
        {
            return null;
        }

        return tenant;
    }

    private static void EnsureNotReadOnly(Tenant tenant)
    {
        if (tenant.IsTrialReadOnly(DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException(
                "This trial has ended. The workspace is read-only until it is purged.");
        }
    }
}
