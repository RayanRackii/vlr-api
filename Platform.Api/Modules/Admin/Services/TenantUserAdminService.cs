using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
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
        CancellationToken cancellationToken);

    Task ResendInviteAsync(Guid tenantId, Guid inviteId, CancellationToken cancellationToken);

    Task RevokeInviteAsync(Guid tenantId, Guid inviteId, CancellationToken cancellationToken);

    Task PromoteAsync(
        Guid tenantId,
        Guid userId,
        PromoteTenantUserRequestDto request,
        CancellationToken cancellationToken);

    Task<AcceptInviteResponseDto> AcceptInviteAsync(
        AcceptInviteRequestDto request,
        CancellationToken cancellationToken);
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
        CancellationToken cancellationToken)
    {
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

        await EnqueueInviteEmailAsync(invite, cancellationToken);

        logger.LogInformation(
            "RBAC invitation created. TenantId={TenantId} InviteId={InviteId}",
            tenantId,
            invite.Id);

        return ToInviteDto(invite);
    }

    public async Task ResendInviteAsync(
        Guid tenantId,
        Guid inviteId,
        CancellationToken cancellationToken)
    {
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

    public async Task<AcceptInviteResponseDto> AcceptInviteAsync(
        AcceptInviteRequestDto request,
        CancellationToken cancellationToken)
    {
        var token = request.Token.Trim();
        if (token.Length == 0)
        {
            throw new ArgumentException("Token is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Password)
            || request.Password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException(
                $"Password must be at least {MinimumPasswordLength} characters.");
        }

        var invite = await dbContext.UserInvites
            .Include(item => item.InviteRoles)
            .FirstOrDefaultAsync(i => i.Token == token, cancellationToken)
            ?? throw new KeyNotFoundException("Invite was not found or is invalid.");

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

        string? supabaseUserId = null;
        var createdAuthUser = false;
        var useTransaction = dbContext.Database.IsRelational();
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
        if (useTransaction)
        {
            transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            supabaseUserId = await supabaseAuthAdminClient.FindUserIdByEmailAsync(
                invite.Email,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(supabaseUserId))
            {
                supabaseUserId = await supabaseAuthAdminClient.CreateUserAsync(
                    invite.Email,
                    request.Password,
                    cancellationToken);
                createdAuthUser = true;
            }
            else
            {
                await supabaseAuthAdminClient.SetUserPasswordAsync(
                    supabaseUserId,
                    request.Password,
                    cancellationToken);
            }

            await supabaseAuthAdminClient.UpdateUserAppMetadataAsync(
                supabaseUserId,
                invite.TenantId,
                cancellationToken);

            var roleIdsToAssign = invite.InviteRoles.Count > 0
                ? invite.InviteRoles.Select(item => item.RoleId).Distinct().ToList()
                : [];

            if (roleIdsToAssign.Count == 0)
            {
                var ensured = await EnsureRoleAsync(invite.TenantId, invite.RoleName, cancellationToken);
                roleIdsToAssign.Add(ensured.Id);
            }

            var user = await dbContext.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    u => u.TenantId == invite.TenantId
                        && (u.SupabaseAuthId == supabaseUserId || u.Email == invite.Email),
                    cancellationToken);

            if (user is null)
            {
                user = new User(
                    invite.TenantId,
                    supabaseUserId,
                    invite.FullName,
                    invite.Email);
                dbContext.Users.Add(user);
                foreach (var roleId in roleIdsToAssign)
                {
                    dbContext.UserRoles.Add(new UserRole(user.Id, roleId));
                }
            }
            else
            {
                user.UpdateProfile(invite.FullName, invite.Email);
                if (!string.Equals(user.SupabaseAuthId, supabaseUserId, StringComparison.Ordinal))
                {
                    user.LinkSupabaseAuthId(supabaseUserId);
                }

                if (!user.IsActive)
                {
                    user.Activate();
                }

                foreach (var roleId in roleIdsToAssign)
                {
                    var hasRole = await dbContext.UserRoles
                        .AnyAsync(
                            ur => ur.UserId == user.Id && ur.RoleId == roleId,
                            cancellationToken);

                    if (!hasRole)
                    {
                        dbContext.UserRoles.Add(new UserRole(user.Id, roleId));
                    }
                }
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
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            if (createdAuthUser && !string.IsNullOrWhiteSpace(supabaseUserId))
            {
                try
                {
                    await supabaseAuthAdminClient.DeleteUserAsync(supabaseUserId, cancellationToken);
                }
                catch (SupabaseAuthAdminException)
                {
                    // best-effort compensation
                }
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
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
            : $"{tenant.Subdomain.Trim().ToLowerInvariant()}.rolvix.com.br";

        var htmlBody = RolvixEmailLayout.Wrap(
            invite.FullName,
            RolvixEmailLayout.InviteBody(inviteUrl, companyName, portalHost));

        await notificationQueue.EnqueueAsync(
            new NotificationMessage(
                Type: "Email",
                Recipient: invite.Email,
                Subject: $"Convite Rolvix — admin de {companyName}",
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
