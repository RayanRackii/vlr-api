using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Services.Billing;

/// <summary>
/// The single gate for plan-driven restrictions: read-only mode and resource limits.
/// Read-only is the trial read-only mode OR a provider-billed subscription in status <c>read_only</c>.
/// Limits come from the plan of the current subscription (<see cref="IPlanLimits"/>); a tenant without a
/// subscription, or with <c>limits_unlimited</c>, is never limited.
/// </summary>
public interface IPlanGuard
{
    Task EnsureWritableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Blocks when the creates would exceed the <c>assets</c> limit and, for rentable ones, the
    /// <c>rentable_resources</c> limit. <paramref name="additionalRentableCount"/> is the part of
    /// <paramref name="additionalCount"/> that is rentable.
    /// </summary>
    Task EnsureCanCreateAssetsAsync(
        int additionalCount,
        int additionalRentableCount,
        CancellationToken cancellationToken);

    Task EnsureCanInviteUserAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Blocks when the tenant already has as many active units as the plan allows.</summary>
    Task EnsureCanCreateUnitAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class PlanGuard(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IPlatformAdminChecker platformAdminChecker) : IPlanGuard
{
    public async Task EnsureWritableAsync(CancellationToken cancellationToken)
    {
        if (tenantProvider.TenantId is not { } tenantId)
        {
            return;
        }

        var tenant = await LoadTenantAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        var state = await PlanStateReader.LoadAsync(dbContext, tenantId, cancellationToken);
        EnsureNotReadOnly(tenant, state);
    }

    public async Task EnsureCanCreateAssetsAsync(
        int additionalCount,
        int additionalRentableCount,
        CancellationToken cancellationToken)
    {
        if (additionalCount < 1)
        {
            return;
        }

        if (tenantProvider.TenantId is not { } tenantId)
        {
            return;
        }

        var tenant = await LoadTenantAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        var state = await PlanStateReader.LoadAsync(dbContext, tenantId, cancellationToken);
        EnsureNotReadOnly(tenant, state);

        if (state is null)
        {
            return;
        }

        var limits = state.EnforcedLimits;
        var rentable = Math.Clamp(additionalRentableCount, 0, additionalCount);

        if (rentable > 0 && limits.RentableResources is { } maxRentable)
        {
            var current = await dbContext.Assets
                .CountAsync(a => a.TenantId == tenantId && a.IsRentable, cancellationToken);
            if (current + rentable > maxRentable)
            {
                throw new PlanLimitReachedException(
                    PlanLimitKeys.RentableResources, current, maxRentable, state.PlanKey);
            }
        }

        if (limits.Assets is { } maxAssets)
        {
            var current = await dbContext.Assets
                .CountAsync(a => a.TenantId == tenantId, cancellationToken);
            if (current + additionalCount > maxAssets)
            {
                throw new PlanLimitReachedException(
                    PlanLimitKeys.Assets, current, maxAssets, state.PlanKey);
            }
        }
    }

    public async Task EnsureCanInviteUserAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await LoadTenantAsync(tenantId, cancellationToken);
        if (tenant is null)
        {
            return;
        }

        var state = await PlanStateReader.LoadAsync(dbContext, tenantId, cancellationToken);
        EnsureNotReadOnly(tenant, state);

        if (state?.EnforcedLimits.Users is not { } maxUsers)
        {
            return;
        }

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

        var current = userCount + pendingInviteCount;
        if (current >= maxUsers)
        {
            throw new PlanLimitReachedException(PlanLimitKeys.Users, current, maxUsers, state.PlanKey);
        }
    }

    public async Task EnsureCanCreateUnitAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var state = await PlanStateReader.LoadAsync(dbContext, tenantId, cancellationToken);
        if (state?.EnforcedLimits.Units is not { } maxUnits)
        {
            return;
        }

        var current = await dbContext.Units
            .CountAsync(u => u.TenantId == tenantId && u.IsActive, cancellationToken);
        if (current >= maxUnits)
        {
            throw new PlanLimitReachedException(PlanLimitKeys.Units, current, maxUnits, state.PlanKey);
        }
    }

    private Task<Tenant?> LoadTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

    private static void EnsureNotReadOnly(Tenant tenant, PlanState? state)
    {
        // A tenant that pays (active, or overdue but still inside the grace window) is never held back by
        // a leftover trial clock: the subscription is the source of truth when there is one.
        var paying = state is { Status: SubscriptionStatuses.Active or SubscriptionStatuses.PastDue };
        if (!paying && tenant.IsTrialReadOnly(DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException(
                "This trial has ended. The workspace is read-only until it is purged.");
        }

        // Only provider-billed rows can be blocked by their status. Manual (backfilled or offline-paid)
        // rows are never made read-only by an automatic transition.
        if (state is { Status: SubscriptionStatuses.ReadOnly, BillingMode: BillingModes.Provider })
        {
            throw new InvalidOperationException(
                "The subscription is overdue. The workspace is read-only until the payment is received.");
        }
    }
}
