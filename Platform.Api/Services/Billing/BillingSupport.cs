using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Services.Billing;

/// <summary>
/// A billing request that cannot proceed. Controllers answer <see cref="HttpStatus"/> with
/// <c>{ error, code, ...details }</c>. The message never carries a provider body or a secret.
/// </summary>
public sealed class BillingException(
    string code,
    int httpStatus,
    string message,
    IReadOnlyDictionary<string, object?>? details = null) : Exception(message)
{
    public string Code { get; } = code;

    public int HttpStatus { get; } = httpStatus;

    public IReadOnlyDictionary<string, object?> Details { get; } =
        details ?? new Dictionary<string, object?>();
}

/// <summary>Stable error codes of the tenant billing endpoints.</summary>
public static class BillingErrorCodes
{
    public const string InvalidCycle = "billing.invalid_cycle";
    public const string InvalidMethod = "billing.invalid_method";
    public const string PlanNotFound = "billing.plan_not_found";
    public const string NoSubscription = "billing.no_subscription";
    public const string NoActiveSubscription = "billing.no_active_subscription";
    public const string AlreadySubscribed = "billing.already_subscribed";
    public const string NoOpenInvoice = "billing.no_open_invoice";
    public const string NothingToCharge = "billing.nothing_to_charge";
    public const string ContactEmailRequired = "billing.contact_email_required";
    public const string TaxIdInvalid = "billing.tax_id_invalid";
    public const string CurrencyMismatch = "billing.currency_mismatch";
    public const string ConcurrentChange = "billing.concurrent_change";
    public const string ProviderError = "billing.provider_error";
    public const string NotConfigured = "billing.not_configured";
    public const string UsageAboveLimit = "plan.usage_above_limit";
}

/// <summary>Price arithmetic of one provider charge.</summary>
public static class BillingPricing
{
    /// <summary>Gross price of one charge of <paramref name="plan"/> for the cycle, in cents.</summary>
    public static int GrossCents(PlanCatalog plan, string billingCycle) =>
        billingCycle == BillingCycles.Annual ? plan.AnnualPriceCents : plan.MonthlyPriceCents;

    /// <summary>Cents of one charge after the discount, rounded half away from zero.</summary>
    public static int ChargeCents(PlanCatalog plan, string billingCycle, int discountPercent) =>
        (int)Math.Round(
            GrossCents(plan, billingCycle) * (100m - Math.Clamp(discountPercent, 0, 100)) / 100m,
            MidpointRounding.AwayFromZero);

    /// <summary>Amount in BRL (not cents) sent to the provider.</summary>
    public static decimal ProviderValue(int chargeCents) => chargeCents / 100m;
}

/// <summary>Resources in use, for the usage bars and the downgrade check.</summary>
public sealed record PlanUsage(int Assets, int RentableResources, int Users, int Units)
{
    /// <summary>
    /// The first limit of <paramref name="limits"/> the usage is above, or null when everything fits.
    /// Using exactly the maximum fits.
    /// </summary>
    public (string Limit, int Current, int Max)? FirstViolation(PlanLimitValues limits)
    {
        if (limits.Assets is { } assets && Assets > assets)
        {
            return (PlanLimitKeys.Assets, Assets, assets);
        }

        if (limits.RentableResources is { } rentable && RentableResources > rentable)
        {
            return (PlanLimitKeys.RentableResources, RentableResources, rentable);
        }

        if (limits.Users is { } users && Users > users)
        {
            return (PlanLimitKeys.Users, Users, users);
        }

        if (limits.Units is { } units && Units > units)
        {
            return (PlanLimitKeys.Units, Units, units);
        }

        return null;
    }

    public PlanUsageDto ToDto() => new(Assets, RentableResources, Users, Units);
}

public interface IPlanUsage
{
    /// <summary>
    /// Usage of the current tenant. Counts carry the explicit tenant id on top of the ambient tenant filter, so
    /// call it from a request of that tenant.
    /// </summary>
    Task<PlanUsage> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Counts like <see cref="PlanGuard"/> does: platform admins are not seats, pending invites are.</summary>
public sealed class PlanUsageService(
    AppDbContext dbContext,
    IPlatformAdminChecker platformAdminChecker,
    TimeProvider timeProvider) : IPlanUsage
{
    public async Task<PlanUsage> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var assets = await dbContext.Assets
            .CountAsync(a => a.TenantId == tenantId, cancellationToken);
        var rentable = await dbContext.Assets
            .CountAsync(a => a.TenantId == tenantId && a.IsRentable, cancellationToken);
        var units = await dbContext.Units
            .CountAsync(u => u.TenantId == tenantId && u.IsActive, cancellationToken);

        var platformEmails = platformAdminChecker.GetNormalizedEmails();
        var now = timeProvider.GetUtcNow();

        var userCount = await dbContext.Users
            .Where(u => u.TenantId == tenantId)
            .Where(u => platformEmails.Count == 0
                || !platformEmails.Contains(u.Email.ToLower()))
            .CountAsync(cancellationToken);

        var pendingInviteCount = await dbContext.UserInvites
            .Where(i =>
                i.TenantId == tenantId
                && i.AcceptedAt == null
                && i.RevokedAt == null
                && i.ExpiresAt > now)
            .Where(i => platformEmails.Count == 0
                || !platformEmails.Contains(i.Email.ToLower()))
            .CountAsync(cancellationToken);

        return new PlanUsage(assets, rentable, userCount + pendingInviteCount, units);
    }
}

/// <summary>
/// Switches ON the modules of a plan the tenant does not have yet. It never switches anything off: disabling stays
/// a platform-admin action (spec 2026-11-21 amendment 3). The caller owns the transaction and the save:
/// <see cref="StageAsync"/> takes the tenant-modules lock, so it must run inside one.
/// </summary>
public interface IPlanModuleEnabler
{
    /// <returns>The module keys that were switched on (staged, not saved).</returns>
    Task<IReadOnlyList<string>> StageAsync(
        Guid tenantId,
        IEnumerable<string> planModules,
        CancellationToken cancellationToken);

    /// <summary>After the commit: refreshes the permissions of the tenant roles for the new modules.</summary>
    Task AfterCommitAsync(Guid tenantId, IReadOnlyList<string> enabled, CancellationToken cancellationToken);
}

public sealed class PlanModuleEnabler(
    AppDbContext dbContext,
    ITenantAccessBootstrapper accessBootstrapper) : IPlanModuleEnabler
{
    /// <summary>Reason stored on a data purge withdrawn because the plan switched the module back on.</summary>
    public const string PlanEnabledReason = "billing.plan_enabled";

    public async Task<IReadOnlyList<string>> StageAsync(
        Guid tenantId,
        IEnumerable<string> planModules,
        CancellationToken cancellationToken)
    {
        var wanted = planModules
            .Select(module => PlatformModules.TryNormalize(module, out var key) ? key : module)
            .Where(module => !PlatformModuleCatalog.IsLegacy(module))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        // Same order as the admin module update: advisory lock first, then the tenant row.
        await TenantModuleLocks.LockModulesAsync(dbContext, tenantId, cancellationToken);
        await TenantModuleLocks.LockTenantRowAsync(dbContext, tenantId, cancellationToken);

        var existing = await dbContext.TenantModules
            .Where(m => m.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var enabled = new List<string>();
        foreach (var module in wanted)
        {
            var row = existing.FirstOrDefault(
                m => string.Equals(m.ModuleName, module, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                dbContext.TenantModules.Add(new TenantModule(tenantId, module, isActive: true));
                enabled.Add(module);
            }
            else if (!row.IsActive)
            {
                row.Activate();
                enabled.Add(module);
            }
        }

        if (enabled.Count == 0)
        {
            return [];
        }

        // A module switched back on withdraws its pending data purge, in the same transaction.
        var pending = await dbContext.TenantModuleDataPurges
            .AsTracking()
            .Where(p => p.TenantId == tenantId
                && p.CancelledAt == null
                && p.ExecutedAt == null
                && enabled.Contains(p.Module))
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var purge in pending)
        {
            purge.Cancel(now, null, PlanEnabledReason);
        }

        // Rentals is left out on purpose: publishing its default menu entry is guarded by a pricing check in
        // the admin flow, which billing does not replicate. The tenant admin adds that entry when ready.
        var existingMenu = await dbContext.TenantModuleMenuItems
            .Where(item => item.TenantId == tenantId)
            .Select(item => item.ModuleName)
            .ToListAsync(cancellationToken);
        ModuleDefaultMenuItems.Ensure(
            dbContext,
            tenantId,
            enabled.Where(module => !string.Equals(module, PlatformModules.Rentals, StringComparison.OrdinalIgnoreCase)),
            existingMenu);

        return enabled;
    }

    public async Task AfterCommitAsync(
        Guid tenantId,
        IReadOnlyList<string> enabled,
        CancellationToken cancellationToken)
    {
        if (enabled.Count > 0)
        {
            await accessBootstrapper.EnsureAsync(tenantId, cancellationToken);
        }
    }
}
