using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Users.Services;

public sealed class UserDirectoryService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IPlatformAdminChecker platformAdminChecker,
    IPermissionResolver permissionResolver,
    IRbacGrantGuard grantGuard,
    ITrialGuard trialGuard,
    NotificationQueue notificationQueue,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<UserDirectoryService> logger) : IUserDirectoryService
{
    private static readonly TimeSpan InviteTtl = TimeSpan.FromDays(7);

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
                        .Include(item => item.UserRoles)
                            .ThenInclude(userRole => userRole.Role)
                        .FirstOrDefaultAsync(
                            item => item.SupabaseAuthId == supabaseAuthId && item.IsActive,
                            cancellationToken);

                    if (membership is not null)
                    {
                        var permissions = await permissionResolver.GetEffectivePermissionsAsync(
                            tenantId,
                            membership.Id,
                            cancellationToken);

                        return CreateCurrentUser(
                            membership.Id,
                            membership.FullName,
                            membership.Email,
                            ApplicationRoles.Admin,
                            tenantId,
                            modules,
                            families,
                            trial,
                            ToRoleDtos(membership.UserRoles),
                            permissions.OrderBy(key => key).ToList());
                    }
                }

                var wildcard = await permissionResolver.GetEnabledCatalogKeysAsync(
                    tenantId,
                    cancellationToken);

                return CreateCurrentUser(
                    null,
                    email,
                    email,
                    ApplicationRoles.Admin,
                    tenantId,
                    modules,
                    families,
                    trial,
                    [],
                    wildcard.OrderBy(key => key).ToList());
            }

            return CreateCurrentUser(
                null,
                email,
                email,
                ApplicationRoles.SuperAdmin,
                null,
                [],
                [],
                TrialFlags.Empty,
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
        var effective = await permissionResolver.GetEffectivePermissionsAsync(
            scopedTenantId,
            user.Id,
            cancellationToken);

        return CreateCurrentUser(
            user.Id,
            user.FullName,
            user.Email,
            role,
            scopedTenantId,
            activeModules,
            activeFamilies,
            trialFlags,
            ToRoleDtos(user.UserRoles),
            effective.OrderBy(key => key).ToList());
    }

    public async Task<IReadOnlyList<TechnicianUserResponse>> ListTechniciansAsync(
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var platformEmails = platformAdminChecker.GetNormalizedEmails();
        var executeKey = Permissions.Os.WorkOrdersExecute;

        var candidates = await dbContext.Users
            .AsNoTracking()
            .Include(user => user.UserRoles)
                .ThenInclude(userRole => userRole.Role)
            .Where(user => user.IsActive)
            .Where(user => platformEmails.Count == 0
                || !platformEmails.Contains(user.Email.ToLower()))
            .OrderBy(user => user.FullName)
            .ToListAsync(cancellationToken);

        var result = new List<TechnicianUserResponse>();
        foreach (var user in candidates)
        {
            if (await permissionResolver.HasPermissionAsync(tenantId, user.Id, executeKey, cancellationToken))
            {
                result.Add(new TechnicianUserResponse(user.Id, user.FullName, user.Email));
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<TenantMemberResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        _ = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var platformEmails = platformAdminChecker.GetNormalizedEmails();

        var users = await dbContext.Users
            .AsNoTracking()
            .Include(user => user.UserRoles)
                .ThenInclude(userRole => userRole.Role)
            .Where(user => platformEmails.Count == 0
                || !platformEmails.Contains(user.Email.ToLower()))
            .OrderBy(user => user.FullName)
            .ToListAsync(cancellationToken);

        return users
            .Select(user => new TenantMemberResponse(
                user.Id,
                user.FullName,
                user.Email,
                user.IsActive,
                ToRoleDtos(user.UserRoles)))
            .ToList();
    }

    public async Task AssignRolesAsync(
        Guid userId,
        RbacActor actor,
        IReadOnlyList<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var tenantId = actor.TenantId;
        var user = await dbContext.Users
            .Include(item => item.UserRoles)
                .ThenInclude(userRole => userRole.Role)
            .FirstOrDefaultAsync(
                item => item.Id == userId && item.TenantId == tenantId,
                cancellationToken)
            ?? throw new KeyNotFoundException("User was not found.");

        if (platformAdminChecker.IsPlatformAdminEmail(user.Email))
        {
            throw new InvalidOperationException(
                "Platform administrators cannot be modified as tenant users.");
        }

        var roles = await LoadAssignableRolesAsync(tenantId, roleIds, cancellationToken);
        await grantGuard.EnsureCanAssignRolesAsync(actor, roles, cancellationToken);
        await grantGuard.EnsureLastAdminNotRemovedAsync(tenantId, user.Id, roles, cancellationToken);

        var existing = await dbContext.UserRoles
            .Where(userRole => userRole.UserId == user.Id)
            .ToListAsync(cancellationToken);
        dbContext.UserRoles.RemoveRange(existing);

        foreach (var role in roles)
        {
            dbContext.UserRoles.Add(new UserRole(user.Id, role.Id));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RBAC user roles changed. TenantId={TenantId} UserId={UserId} RoleCount={RoleCount}",
            tenantId,
            user.Id,
            roles.Count);
    }

    public async Task<InviteTenantMemberResponse> InviteAsync(
        RbacActor actor,
        InviteTenantMemberRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = actor.TenantId;
        await trialGuard.EnsureCanInviteUserAsync(tenantId, cancellationToken);

        var fullName = request.FullName.Trim();
        var email = request.Email.Trim().ToLowerInvariant();
        if (fullName.Length < 2)
        {
            throw new ArgumentException("FullName is required.");
        }

        if (!IsValidEmail(email))
        {
            throw new ArgumentException("Email is not valid.");
        }

        if (platformAdminChecker.IsPlatformAdminEmail(email))
        {
            throw new InvalidOperationException(
                "Platform administrators cannot be invited as tenant users.");
        }

        if (request.RoleIds is null || request.RoleIds.Count == 0)
        {
            throw new ArgumentException("At least one role is required.");
        }

        var roles = await LoadAssignableRolesAsync(tenantId, request.RoleIds, cancellationToken);
        await grantGuard.EnsureCanAssignRolesAsync(actor, roles, cancellationToken);

        var existingUser = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(user => user.TenantId == tenantId && user.Email == email, cancellationToken);
        if (existingUser)
        {
            throw new InvalidOperationException(
                "A user with this email already exists for the tenant.");
        }

        var pending = await dbContext.UserInvites
            .Where(invite => invite.TenantId == tenantId
                && invite.Email == email
                && invite.AcceptedAt == null
                && invite.RevokedAt == null
                && invite.ExpiresAt > DateTimeOffset.UtcNow)
            .ToListAsync(cancellationToken);

        foreach (var old in pending)
        {
            old.Revoke();
        }

        var primaryRoleName = roles
            .First(role => role.Id == request.RoleIds[0])
            .Name;

        var invite = new UserInvite(
            tenantId,
            email,
            fullName,
            primaryRoleName,
            GenerateToken(),
            DateTimeOffset.UtcNow.Add(InviteTtl));

        dbContext.UserInvites.Add(invite);
        foreach (var role in roles)
        {
            dbContext.UserInviteRoles.Add(new UserInviteRole(invite.Id, role.Id));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await EnqueueInviteEmailAsync(invite, cancellationToken);

        logger.LogInformation(
            "RBAC invitation created. TenantId={TenantId} InviteId={InviteId} RoleCount={RoleCount}",
            tenantId,
            invite.Id,
            roles.Count);

        return new InviteTenantMemberResponse(
            invite.Id,
            invite.FullName,
            invite.Email,
            invite.RoleName,
            invite.ExpiresAt);
    }

    private async Task<IReadOnlyList<Role>> LoadAssignableRolesAsync(
        Guid tenantId,
        IReadOnlyList<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var distinctIds = roleIds.Distinct().ToList();
        var roles = await dbContext.Roles
            .Include(role => role.RolePermissions)
                .ThenInclude(rolePermission => rolePermission.Permission)
            .Where(role => role.TenantId == tenantId && distinctIds.Contains(role.Id))
            .ToListAsync(cancellationToken);

        if (roles.Count != distinctIds.Count)
        {
            throw new ArgumentException("One or more roles were not found.");
        }

        return roles;
    }

    private async Task EnqueueInviteEmailAsync(UserInvite invite, CancellationToken cancellationToken)
    {
        var frontendBaseUrl = FrontendBaseUrlResolver.Resolve(configuration, environment);
        var inviteUrl = $"{frontendBaseUrl}/invite?token={Uri.EscapeDataString(invite.Token)}";

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .Where(item => item.Id == invite.TenantId)
            .Select(item => new { item.LegalName, item.Subdomain })
            .FirstOrDefaultAsync(cancellationToken);

        var companyName = string.IsNullOrWhiteSpace(tenant?.LegalName)
            ? "sua empresa"
            : tenant.LegalName;

        var portalHost = string.IsNullOrWhiteSpace(tenant?.Subdomain)
            ? null
            : $"{tenant.Subdomain.Trim().ToLowerInvariant()}.rolvix.com.br";

        var htmlBody = RolvixEmailLayout.Wrap(
            invite.FullName,
            RolvixEmailLayout.InviteBody(inviteUrl, companyName, portalHost));

        await notificationQueue.EnqueueAsync(
            new NotificationMessage(
                Type: "Email",
                Recipient: invite.Email,
                Subject: $"Convite Rolvix — {companyName}",
                Body: htmlBody),
            cancellationToken);
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

    private static CurrentUserResponse CreateCurrentUser(
        Guid? id,
        string fullName,
        string email,
        string role,
        Guid? tenantId,
        IReadOnlyList<string> activeModules,
        IReadOnlyList<string> activeAssetFamilies,
        TrialFlags trial,
        IReadOnlyList<CurrentUserRoleDto> roles,
        IReadOnlyList<string> permissions) =>
        new(
            id,
            fullName,
            email,
            role,
            tenantId,
            activeModules,
            activeAssetFamilies,
            trial.IsTrial,
            trial.TrialEndsAt,
            trial.TrialPurgeAt,
            trial.IsTrialReadOnly,
            trial.NotificationsEmailOnly,
            roles,
            permissions);

    private static IReadOnlyList<CurrentUserRoleDto> ToRoleDtos(IEnumerable<UserRole> userRoles) =>
        userRoles
            .Select(userRole => new CurrentUserRoleDto(
                userRole.Role.Id,
                userRole.Role.Name,
                userRole.Role.IsSystemRole))
            .OrderBy(role => role.Name)
            .ToList();

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

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            _ = new MailAddress(email);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

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
