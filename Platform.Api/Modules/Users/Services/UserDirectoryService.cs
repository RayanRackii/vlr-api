using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Modules.Users.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Users.Services;

public sealed class UserDirectoryService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IPlatformAdminChecker platformAdminChecker) : IUserDirectoryService
{
    public async Task<CurrentUserResponse> GetCurrentAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (platformAdminChecker.IsPlatformAdmin(principal))
        {
            var email = ResolveEmail(principal) ?? string.Empty;

            if (tenantProvider.TenantId is Guid tenantId)
            {
                var modules = await LoadActiveModulesAsync(tenantId, cancellationToken);
                var families = await LoadActiveAssetFamiliesAsync(tenantId, cancellationToken);
                var trial = await LoadTrialFlagsAsync(tenantId, cancellationToken);
                var supabaseAuthId = principal.FindFirst("sub")?.Value
                    ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                if (!string.IsNullOrWhiteSpace(supabaseAuthId))
                {
                    var membership = await dbContext.Users
                        .AsNoTracking()
                        .FirstOrDefaultAsync(
                            item => item.SupabaseAuthId == supabaseAuthId && item.IsActive,
                            cancellationToken);

                    if (membership is not null)
                    {
                        return new CurrentUserResponse(
                            membership.Id,
                            membership.FullName,
                            membership.Email,
                            ApplicationRoles.Admin,
                            tenantId,
                            modules,
                            families,
                            trial.IsTrial,
                            trial.TrialEndsAt,
                            trial.TrialPurgeAt,
                            trial.IsTrialReadOnly,
                            trial.NotificationsEmailOnly);
                    }
                }

                return new CurrentUserResponse(
                    null,
                    email,
                    email,
                    ApplicationRoles.Admin,
                    tenantId,
                    modules,
                    families,
                    trial.IsTrial,
                    trial.TrialEndsAt,
                    trial.TrialPurgeAt,
                    trial.IsTrialReadOnly,
                    trial.NotificationsEmailOnly);
            }

            return new CurrentUserResponse(
                null,
                email,
                email,
                ApplicationRoles.SuperAdmin,
                null,
                [],
                []);
        }

        var scopedTenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var authId = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("The authenticated user identifier is missing.");

        var user = await dbContext.Users
            .AsNoTracking()
            .Include(item => item.UserRoles)
                .ThenInclude(userRole => userRole.Role)
            .FirstOrDefaultAsync(
                item => item.SupabaseAuthId == authId && item.IsActive,
                cancellationToken)
            ?? throw new KeyNotFoundException("The authenticated user profile was not found.");

        var role = ResolveApplicationRole(user.UserRoles.Select(userRole => userRole.Role.Name));
        var activeModules = await LoadActiveModulesAsync(scopedTenantId, cancellationToken);
        var activeFamilies = await LoadActiveAssetFamiliesAsync(scopedTenantId, cancellationToken);
        var trialFlags = await LoadTrialFlagsAsync(scopedTenantId, cancellationToken);

        return new CurrentUserResponse(
            user.Id,
            user.FullName,
            user.Email,
            role,
            scopedTenantId,
            activeModules,
            activeFamilies,
            trialFlags.IsTrial,
            trialFlags.TrialEndsAt,
            trialFlags.TrialPurgeAt,
            trialFlags.IsTrialReadOnly,
            trialFlags.NotificationsEmailOnly);
    }

    public async Task<IReadOnlyList<TechnicianUserResponse>> ListTechniciansAsync(
        CancellationToken cancellationToken)
    {
        _ = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        return await dbContext.Users
            .AsNoTracking()
            .Where(user =>
                user.IsActive
                && user.UserRoles.Any(userRole =>
                    EF.Functions.ILike(userRole.Role.Name, SystemRoles.Technician)))
            .OrderBy(user => user.FullName)
            .Select(user => new TechnicianUserResponse(
                user.Id,
                user.FullName,
                user.Email))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadActiveModulesAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        return await dbContext.TenantModules
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(module => module.TenantId == tenantId && module.IsActive)
            .Select(module => module.ModuleName.ToLowerInvariant())
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> LoadActiveAssetFamiliesAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        return await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Join(
                dbContext.AssetFamilies.AsNoTracking().Where(f => f.IsActive),
                t => t.FamilyId,
                f => f.Id,
                (_, f) => f)
            .OrderBy(f => f.SortOrder)
            .Select(f => f.Key)
            .ToListAsync(cancellationToken);
    }

    private async Task<TrialFlags> LoadTrialFlagsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return TrialFlags.Empty;
        }

        return new TrialFlags(
            tenant.IsTrial,
            tenant.TrialEndsAt,
            tenant.TrialPurgeAt,
            tenant.IsTrialReadOnly(DateTimeOffset.UtcNow),
            tenant.NotificationsEmailOnly);
    }

    private static string ResolveApplicationRole(IEnumerable<string> roleNames)
    {
        var normalizedRoles = roleNames
            .Select(NormalizeRole)
            .ToHashSet(StringComparer.Ordinal);

        if (normalizedRoles.Contains("SUPERADMIN") || normalizedRoles.Contains("ADMIN"))
        {
            return ApplicationRoles.Admin;
        }

        if (normalizedRoles.Contains("TECHNICIAN"))
        {
            return ApplicationRoles.Technician;
        }

        if (normalizedRoles.Contains("CLIENT"))
        {
            return ApplicationRoles.Client;
        }

        return ApplicationRoles.User;
    }

    private static string NormalizeRole(string role) =>
        role.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

    private static string? ResolveEmail(ClaimsPrincipal principal) =>
        principal.FindFirst("email")?.Value
        ?? principal.FindFirst(ClaimTypes.Email)?.Value
        ?? principal.Identity?.Name;

    private readonly record struct TrialFlags(
        bool IsTrial,
        DateTimeOffset? TrialEndsAt,
        DateTimeOffset? TrialPurgeAt,
        bool IsTrialReadOnly,
        bool NotificationsEmailOnly)
    {
        public static TrialFlags Empty { get; } = new(false, null, null, false, false);
    }
}
