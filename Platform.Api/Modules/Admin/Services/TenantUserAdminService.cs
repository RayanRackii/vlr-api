using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Admin.Services;

public interface ITenantUserAdminService
{
    Task<TenantUsersBundleDto> ListUsersAndInvitesAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<TenantInviteResponseDto> InviteAsync(
        Guid tenantId,
        InviteTenantUserRequestDto request,
        CancellationToken cancellationToken,
        bool enqueueEmail = true);

    Task DeliverInviteEmailAsync(
        Guid tenantId,
        Guid inviteId,
        CancellationToken cancellationToken);

    Task ResendInviteAsync(Guid tenantId, Guid inviteId, CancellationToken cancellationToken);

    Task RevokeInviteAsync(Guid tenantId, Guid inviteId, CancellationToken cancellationToken);

    Task PromoteAsync(
        Guid tenantId,
        Guid userId,
        PromoteTenantUserRequestDto request,
        CancellationToken cancellationToken);

    Task<InvitePreviewResponseDto> PreviewInviteAsync(
        string token,
        CancellationToken cancellationToken);

    /// <param name="actor">
    /// Staff session, when the caller sent a bearer token. Required to link an email
    /// that already has an account. Anonymous callers still activate a new account.
    /// </param>
    Task<AcceptInviteResponseDto> AcceptInviteAsync(
        AcceptInviteRequestDto request,
        CancellationToken cancellationToken,
        ClaimsPrincipal? actor = null);
}

public sealed class TenantUserAdminService(
    AppDbContext dbContext,
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    NotificationQueue notificationQueue,
    IConfiguration configuration,
    IHostEnvironment environment,
    ITrialGuard trialGuard,
    IPlatformAdminChecker platformAdminChecker,
    ILogger<TenantUserAdminService> logger) : ITenantUserAdminService
{
    private static readonly TimeSpan InviteTtl = TimeSpan.FromDays(7);
    private const int MinimumPasswordLength = 8;

    private static readonly HashSet<string> AssignableRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        SystemRoles.Admin,
        SystemRoles.Technician,
        SystemRoles.User,
        SystemRoles.Client,
        SystemRoles.SuperAdmin,
    };

    public async Task<TenantUsersBundleDto> ListUsersAndInvitesAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // Tenant-id methods run for a Super-Admin (maybe without a tenant) or tenant provisioning;
        // every query keeps the explicit tenantId predicate.
        using var _ = PlatformScope.Begin(logger, "admin-tenant-users-list");
        await EnsureTenantExistsAsync(tenantId, cancellationToken);

        var platformEmails = platformAdminChecker.GetNormalizedEmails();

        var users = await dbContext.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .Where(u => u.TenantId == tenantId)
            .Where(u => platformEmails.Count == 0
                || !platformEmails.Contains(u.Email.ToLower()))
            .OrderBy(u => u.FullName)
            .ToListAsync(cancellationToken);

        var invites = await dbContext.UserInvites
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .Where(i => platformEmails.Count == 0
                || !platformEmails.Contains(i.Email.ToLower()))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);

        return new TenantUsersBundleDto(
            users.Select(u => new TenantUserResponseDto(
                u.Id,
                u.FullName,
                u.Email,
                u.IsActive,
                u.UserRoles.Select(ur => ur.Role.Name).Distinct().OrderBy(n => n).ToList(),
                u.CreatedAt)).ToList(),
            invites.Select(ToInviteDto).ToList());
    }

    public async Task<TenantInviteResponseDto> InviteAsync(
        Guid tenantId,
        InviteTenantUserRequestDto request,
        CancellationToken cancellationToken,
        bool enqueueEmail = true)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-users-invite");
        await EnsureTenantExistsAsync(tenantId, cancellationToken);
        await trialGuard.EnsureCanInviteUserAsync(tenantId, cancellationToken);

        var fullName = request.FullName.Trim();
        var email = NormalizeEmail(request.Email);

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

        var existingUser = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(u => u.TenantId == tenantId && u.Email == email, cancellationToken);
        if (existingUser)
        {
            throw new InvalidOperationException(
                "A user with this email already exists for the tenant. Promote them instead.");
        }

        IReadOnlyList<Role> inviteRoles = [];
        string roleName;
        if (request.RoleIds is { Count: > 0 })
        {
            inviteRoles = await LoadRolesAsync(tenantId, request.RoleIds, cancellationToken);
            roleName = inviteRoles.First(role => role.Id == request.RoleIds![0]).Name;
        }
        else
        {
            roleName = NormalizeRole(request.RoleName);
        }

        var pending = await dbContext.UserInvites
            .Where(i => i.TenantId == tenantId
                        && i.Email == email
                        && i.AcceptedAt == null
                        && i.RevokedAt == null
                        && i.ExpiresAt > DateTimeOffset.UtcNow)
            .ToListAsync(cancellationToken);

        foreach (var old in pending)
        {
            old.Revoke();
        }

        var invite = new UserInvite(
            tenantId,
            email,
            fullName,
            roleName,
            GenerateToken(),
            DateTimeOffset.UtcNow.Add(InviteTtl));

        dbContext.UserInvites.Add(invite);
        foreach (var role in inviteRoles)
        {
            dbContext.UserInviteRoles.Add(new UserInviteRole(invite.Id, role.Id));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (enqueueEmail)
        {
            await EnqueueInviteEmailAsync(invite, cancellationToken);
        }

        logger.LogInformation(
            "RBAC invitation created. TenantId={TenantId} InviteId={InviteId}",
            tenantId,
            invite.Id);

        return ToInviteDto(invite);
    }

    public async Task DeliverInviteEmailAsync(
        Guid tenantId,
        Guid inviteId,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-users-deliver-invite");
        var invite = await dbContext.UserInvites
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == inviteId && item.TenantId == tenantId,
                cancellationToken)
            ?? throw new KeyNotFoundException("Invite was not found.");

        await EnqueueInviteEmailAsync(invite, cancellationToken);
    }

    public async Task ResendInviteAsync(
        Guid tenantId,
        Guid inviteId,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-users-resend-invite");
        var invite = await dbContext.UserInvites
            .Include(item => item.InviteRoles)
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Invite was not found.");

        if (invite.AcceptedAt is not null)
        {
            throw new InvalidOperationException("Invite was already accepted.");
        }

        if (invite.RevokedAt is not null)
        {
            throw new InvalidOperationException("Invite was revoked.");
        }

        // Rotate token + extend expiry on resend.
        var refreshed = new UserInvite(
            invite.TenantId,
            invite.Email,
            invite.FullName,
            invite.RoleName,
            GenerateToken(),
            DateTimeOffset.UtcNow.Add(InviteTtl));

        invite.Revoke();
        dbContext.UserInvites.Add(refreshed);
        foreach (var inviteRole in invite.InviteRoles)
        {
            dbContext.UserInviteRoles.Add(new UserInviteRole(refreshed.Id, inviteRole.RoleId));
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        await EnqueueInviteEmailAsync(refreshed, cancellationToken);
    }

    public async Task RevokeInviteAsync(
        Guid tenantId,
        Guid inviteId,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-users-revoke-invite");
        var invite = await dbContext.UserInvites
            .FirstOrDefaultAsync(i => i.Id == inviteId && i.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Invite was not found.");

        if (invite.AcceptedAt is not null)
        {
            throw new InvalidOperationException("Invite was already accepted.");
        }

        if (invite.RevokedAt is null)
        {
            invite.Revoke();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task PromoteAsync(
        Guid tenantId,
        Guid userId,
        PromoteTenantUserRequestDto request,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-users-promote");
        await EnsureTenantExistsAsync(tenantId, cancellationToken);
        var roleName = NormalizeRole(request.RoleName);

        var user = await dbContext.Users
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId && u.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("User was not found.");

        if (platformAdminChecker.IsPlatformAdminEmail(user.Email))
        {
            throw new InvalidOperationException(
                "Platform administrators cannot be modified as tenant users.");
        }

        if (user.UserRoles.Any(ur =>
                string.Equals(ur.Role.Name, roleName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var role = await EnsureRoleAsync(tenantId, roleName, cancellationToken);
        dbContext.UserRoles.Add(new UserRole(user.Id, role.Id));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvitePreviewResponseDto> PreviewInviteAsync(
        string token,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "staff-invite-preview");
        var invite = await LoadPendingInviteAsync(token, cancellationToken);
        var existingAuthId = await ResolveExistingAuthIdAsync(invite.Email, cancellationToken);
        var tenantName = await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == invite.TenantId)
            .Select(tenant => tenant.TradeName ?? tenant.LegalName)
            .FirstOrDefaultAsync(cancellationToken);

        return new InvitePreviewResponseDto(
            invite.Email,
            invite.FullName,
            invite.TenantId,
            tenantName ?? string.Empty,
            ExistingAccount: existingAuthId is not null,
            invite.ExpiresAt);
    }

    public async Task<AcceptInviteResponseDto> AcceptInviteAsync(
        AcceptInviteRequestDto request,
        CancellationToken cancellationToken,
        ClaimsPrincipal? actor = null)
    {
        using var _ = PlatformScope.Begin(logger, "staff-invite-accept");
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        var invite = await LoadPendingInviteAsync(request.Token, cancellationToken);
        await LockInviteAsync(invite, cancellationToken);
        var existingAuthId = await ResolveExistingAuthIdAsync(invite.Email, cancellationToken);
        if (existingAuthId is not null)
        {
            EnsureExistingAccountActor(actor, invite.Email, existingAuthId);
            var linked = await LinkExistingMembershipAsync(
                invite,
                existingAuthId,
                cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
            return linked;
        }

        var password = request.Password ?? string.Empty;
        if (password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"Password must be at least {MinimumPasswordLength} characters.");
        }

        return await CreateNewAccountAsync(invite, password, transaction, cancellationToken);
    }

    private async Task LockInviteAsync(UserInvite invite, CancellationToken cancellationToken)
    {
        if (dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true)
        {
            return;
        }

        var key = $"staff-invite:{invite.Email}";
        const long seed = 0;
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, {seed}))",
            cancellationToken);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT id FROM core.user_invites
            WHERE id = {invite.Id} AND tenant_id = {invite.TenantId}
            FOR UPDATE
            """,
            cancellationToken);
        await dbContext.Entry(invite).ReloadAsync(cancellationToken);
        if (dbContext.Entry(invite).State == EntityState.Detached)
        {
            throw new KeyNotFoundException("Invite was not found or is invalid.");
        }
        EnsurePendingInvite(invite);
    }

    private async Task<UserInvite> LoadPendingInviteAsync(
        string? token,
        CancellationToken cancellationToken)
    {
        var normalized = token?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Token is required.");
        }

        // The token is the lookup key. A tenant filter on the request must not hide it
        // or make an existing account in another tenant look new.
        var invite = await dbContext.UserInvites
            .IgnoreQueryFilters()
            .Include(item => item.InviteRoles)
            .FirstOrDefaultAsync(item => item.Token == normalized, cancellationToken)
            ?? throw new KeyNotFoundException("Invite was not found or is invalid.");

        EnsurePendingInvite(invite);
        return invite;
    }

    private static void EnsurePendingInvite(UserInvite invite)
    {
        if (invite.RevokedAt is not null)
        {
            throw new InvalidOperationException("Invite was revoked.");
        }

        if (invite.AcceptedAt is not null)
        {
            throw new InvalidOperationException("Invite was already accepted.");
        }

        if (invite.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("Invite has expired.");
        }
    }

    /// <summary>
    /// Auth id of an account that already owns this email, or null when the email is new.
    /// Local memberships are read across tenants. A disagreement fails closed.
    /// </summary>
    private async Task<string?> ResolveExistingAuthIdAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var authId = await supabaseAuthAdminClient.FindUserIdByEmailAsync(
            email,
            cancellationToken);

        var localIds = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(user => user.Email.ToLower() == email)
            .Select(user => user.SupabaseAuthId)
            .ToListAsync(cancellationToken);

        var distinctLocal = localIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctLocal.Count > 1)
        {
            throw new InviteAcceptanceException(
                InviteAcceptanceCodes.IdentityConflict,
                StatusCodes.Status409Conflict);
        }

        var localId = distinctLocal.Count == 1 ? distinctLocal[0] : null;
        if (!string.IsNullOrWhiteSpace(authId)
            && localId is not null
            && !string.Equals(localId, authId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InviteAcceptanceException(
                InviteAcceptanceCodes.IdentityConflict,
                StatusCodes.Status409Conflict);
        }

        if (!string.IsNullOrWhiteSpace(authId))
        {
            return authId.Trim();
        }

        return localId;
    }

    private static void EnsureExistingAccountActor(
        ClaimsPrincipal? actor,
        string email,
        string authId)
    {
        if (actor?.Identity?.IsAuthenticated != true || CustomerClaimTypes.IsCustomer(actor))
        {
            throw new InviteAcceptanceException(
                InviteAcceptanceCodes.ExistingAccountSignInRequired,
                StatusCodes.Status409Conflict);
        }

        var actorEmail = actor.FindFirst("email")?.Value
            ?? actor.FindFirst(ClaimTypes.Email)?.Value;
        var actorSub = actor.FindFirst("sub")?.Value
            ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        var emailMatches = !string.IsNullOrWhiteSpace(actorEmail)
            && string.Equals(actorEmail.Trim(), email, StringComparison.OrdinalIgnoreCase);
        var subMatches = !string.IsNullOrWhiteSpace(actorSub)
            && string.Equals(actorSub.Trim(), authId, StringComparison.OrdinalIgnoreCase);

        if (!emailMatches || !subMatches)
        {
            throw new InviteAcceptanceException(
                InviteAcceptanceCodes.AccountMismatch,
                StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>
    /// Links the inviting tenant only. Must not call <c>SetUserPassword</c>,
    /// <c>CreateUser</c>, or <c>UpdateUserAppMetadata</c>: the Auth password and the
    /// caller's current tenant stay as they were.
    /// </summary>
    private async Task<AcceptInviteResponseDto> LinkExistingMembershipAsync(
        UserInvite invite,
        string supabaseUserId,
        CancellationToken cancellationToken)
    {
        var roleIds = await ResolveInviteRoleIdsAsync(invite, cancellationToken);
        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                item => item.TenantId == invite.TenantId && item.Email.ToLower() == invite.Email,
                cancellationToken);

        if (user is not null
            && !string.Equals(user.SupabaseAuthId, supabaseUserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InviteAcceptanceException(
                InviteAcceptanceCodes.IdentityConflict,
                StatusCodes.Status409Conflict);
        }

        if (user is not null && !user.IsActive)
        {
            throw new InvalidOperationException(
                "A user with this email already exists for the tenant.");
        }

        if (user is null)
        {
            user = new User(
                invite.TenantId,
                supabaseUserId,
                invite.FullName,
                invite.Email);
            dbContext.Users.Add(user);
        }

        foreach (var roleId in roleIds)
        {
            var hasRole = await dbContext.UserRoles
                .IgnoreQueryFilters()
                .AnyAsync(
                    userRole => userRole.UserId == user.Id && userRole.RoleId == roleId,
                    cancellationToken);
            if (!hasRole)
            {
                dbContext.UserRoles.Add(new UserRole(user.Id, roleId));
            }
        }

        invite.MarkAccepted(user.Id);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RBAC invitation linked an existing account. TenantId={TenantId} InviteId={InviteId} UserId={UserId}",
            invite.TenantId,
            invite.Id,
            user.Id);

        return new AcceptInviteResponseDto(
            user.Id,
            invite.TenantId,
            invite.Email,
            ExistingAccount: true);
    }

    private async Task<AcceptInviteResponseDto> CreateNewAccountAsync(
        UserInvite invite,
        string password,
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        string? supabaseUserId = null;
        var createdAuthUser = false;
        const string savepoint = "before_auth_provisioning";
        if (transaction is not null)
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        }

        try
        {
            supabaseUserId = await supabaseAuthAdminClient.CreateUserAsync(
                invite.Email,
                password,
                cancellationToken);
            createdAuthUser = true;

            await supabaseAuthAdminClient.UpdateUserAppMetadataAsync(
                supabaseUserId,
                invite.TenantId,
                cancellationToken);

            var roleIds = await ResolveInviteRoleIdsAsync(invite, cancellationToken);
            var user = new User(
                invite.TenantId,
                supabaseUserId,
                invite.FullName,
                invite.Email);
            dbContext.Users.Add(user);
            foreach (var roleId in roleIds)
            {
                dbContext.UserRoles.Add(new UserRole(user.Id, roleId));
            }

            invite.MarkAccepted(user.Id);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            logger.LogInformation(
                "RBAC invitation accepted. TenantId={TenantId} InviteId={InviteId} UserId={UserId}",
                invite.TenantId,
                invite.Id,
                user.Id);

            return new AcceptInviteResponseDto(user.Id, invite.TenantId, invite.Email);
        }
        catch
        {
            if (createdAuthUser && !string.IsNullOrWhiteSpace(supabaseUserId))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    if (transaction is not null)
                    {
                        await transaction.RollbackToSavepointAsync(savepoint, cleanup.Token);
                    }

                    var normalizedAuthId = supabaseUserId.ToLowerInvariant();
                    var hasMembership = await dbContext.Users
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .AnyAsync(user => user.SupabaseAuthId.ToLower() == normalizedAuthId, cleanup.Token);
                    if (hasMembership)
                    {
                        logger.LogWarning(
                            "Preserving Auth user {AuthUserId} after failed invite {InviteId}: a membership exists.",
                            supabaseUserId,
                            invite.Id);
                    }
                    else
                    {
                        await supabaseAuthAdminClient.DeleteUserAsync(supabaseUserId, cleanup.Token);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Could not safely compensate Auth user {AuthUserId} after failed invite {InviteId}; recovery is required.",
                        supabaseUserId,
                        invite.Id);
                }
            }

            throw;
        }
    }

    private async Task<List<Guid>> ResolveInviteRoleIdsAsync(
        UserInvite invite,
        CancellationToken cancellationToken)
    {
        var requested = invite.InviteRoles
            .Select(item => item.RoleId)
            .Distinct()
            .ToList();

        var roleIds = requested.Count == 0
            ? []
            : await dbContext.Roles
                .IgnoreQueryFilters()
                .Where(role => requested.Contains(role.Id) && role.TenantId == invite.TenantId)
                .Select(role => role.Id)
                .ToListAsync(cancellationToken);

        if (roleIds.Count == 0)
        {
            var ensured = await EnsureRoleAsync(invite.TenantId, invite.RoleName, cancellationToken);
            roleIds.Add(ensured.Id);
        }

        return roleIds;
    }

    private async Task EnsureTenantExistsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Tenants.AsNoTracking()
            .AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Tenant was not found.");
        }
    }

    private async Task<IReadOnlyList<Role>> LoadRolesAsync(
        Guid tenantId,
        IReadOnlyList<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var distinctIds = roleIds.Distinct().ToList();
        var roles = await dbContext.Roles
            .Where(role => role.TenantId == tenantId && distinctIds.Contains(role.Id))
            .ToListAsync(cancellationToken);

        if (roles.Count != distinctIds.Count)
        {
            throw new ArgumentException("One or more roles were not found.");
        }

        return roles;
    }

    private async Task<Role> EnsureRoleAsync(
        Guid tenantId,
        string roleName,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.Roles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId && r.Name.ToLower() == roleName.ToLower(),
                cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var role = new Role(
            tenantId,
            roleName,
            $"{roleName} (system)",
            isSystemRole: true);

        dbContext.Roles.Add(role);
        await dbContext.SaveChangesAsync(cancellationToken);
        return role;
    }

    private async Task EnqueueInviteEmailAsync(UserInvite invite, CancellationToken cancellationToken)
    {
        var frontendBaseUrl = FrontendBaseUrlResolver.Resolve(configuration, environment);
        var inviteUrl = $"{frontendBaseUrl}/invite?token={Uri.EscapeDataString(invite.Token)}";

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .Where(t => t.Id == invite.TenantId)
            .Select(t => new { t.LegalName, t.Subdomain })
            .FirstOrDefaultAsync(cancellationToken);

        var companyName = string.IsNullOrWhiteSpace(tenant?.LegalName)
            ? "sua empresa"
            : tenant.LegalName;

        var portalHost = string.IsNullOrWhiteSpace(tenant?.Subdomain)
            ? null
            : FrontendBaseUrlResolver.ResolveTenantPortalHost(configuration, tenant.Subdomain);
        var tenantPortalHostTemplate = FrontendBaseUrlResolver.ResolveTenantPortalHostTemplate(configuration);

        var roleNames = await StaffInviteRoleNames.LoadAsync(
            dbContext,
            invite.TenantId,
            invite.Id,
            invite.RoleName,
            cancellationToken);

        var htmlBody = RolvixEmailLayout.Wrap(
            invite.FullName,
            RolvixEmailLayout.InviteBody(
                inviteUrl,
                companyName,
                portalHost,
                roleNames: roleNames,
                frontendBaseUrl: frontendBaseUrl,
                tenantPortalHostTemplate: tenantPortalHostTemplate));

        await notificationQueue.EnqueueAsync(
            new NotificationMessage(
                Type: "Email",
                Recipient: invite.Email,
                Subject: RolvixEmailLayout.InviteSubject(companyName, roleNames),
                Body: htmlBody),
            cancellationToken);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string NormalizeRole(string? roleName)
    {
        var name = string.IsNullOrWhiteSpace(roleName)
            ? SystemRoles.Admin
            : roleName.Trim();

        foreach (var known in AssignableRoles)
        {
            if (known.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        throw new ArgumentException($"Role '{name}' is not assignable.");
    }

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

    private static TenantInviteResponseDto ToInviteDto(UserInvite invite) =>
        new(
            invite.Id,
            invite.FullName,
            invite.Email,
            invite.RoleName,
            invite.ExpiresAt,
            invite.CreatedAt,
            invite.IsPending,
            invite.AcceptedAt,
            invite.RevokedAt);
}
