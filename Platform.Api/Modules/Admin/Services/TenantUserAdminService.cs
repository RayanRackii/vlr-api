using System.Net.Mail;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Notifications;
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
    IConfiguration configuration) : ITenantUserAdminService
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

        var users = await dbContext.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.FullName)
            .ToListAsync(cancellationToken);

        var invites = await dbContext.UserInvites
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId)
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

        var fullName = request.FullName.Trim();
        var email = NormalizeEmail(request.Email);
        var roleName = NormalizeRole(request.RoleName);

        if (fullName.Length < 2)
        {
            throw new ArgumentException("FullName is required.");
        }

        if (!IsValidEmail(email))
        {
            throw new ArgumentException("Email is not valid.");
        }

        var existingUser = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(u => u.TenantId == tenantId && u.Email == email, cancellationToken);
        if (existingUser)
        {
            throw new InvalidOperationException(
                "A user with this email already exists for the tenant. Promote them instead.");
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
        await dbContext.SaveChangesAsync(cancellationToken);

        await EnqueueInviteEmailAsync(invite, cancellationToken);

        return ToInviteDto(invite);
    }

    public async Task ResendInviteAsync(
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
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            supabaseUserId = await supabaseAuthAdminClient.CreateUserAsync(
                invite.Email,
                request.Password,
                cancellationToken);

            await supabaseAuthAdminClient.UpdateUserAppMetadataAsync(
                supabaseUserId,
                invite.TenantId,
                cancellationToken);

            var role = await EnsureRoleAsync(invite.TenantId, invite.RoleName, cancellationToken);

            var user = new User(
                invite.TenantId,
                supabaseUserId,
                invite.FullName,
                invite.Email);

            dbContext.Users.Add(user);
            dbContext.UserRoles.Add(new UserRole(user.Id, role.Id));
            invite.MarkAccepted(user.Id);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new AcceptInviteResponseDto(user.Id, invite.TenantId, invite.Email);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(supabaseUserId))
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

    private async Task<Role> EnsureRoleAsync(
        Guid tenantId,
        string roleName,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.Roles
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId && EF.Functions.ILike(r.Name, roleName),
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
        var frontendBaseUrl = configuration["App:FrontendBaseUrl"]?.TrimEnd('/')
            ?? "http://localhost:5173";

        var inviteUrl = $"{frontendBaseUrl}/invite?token={Uri.EscapeDataString(invite.Token)}";

        var htmlBody =
            $"""
            <html>
              <body>
                <p>Olá {System.Net.WebUtility.HtmlEncode(invite.FullName)},</p>
                <p>Você foi convidado(a) para o Rolvix.</p>
                <p>Defina sua senha neste link (válido por 7 dias):</p>
                <p><a href="{inviteUrl}">{inviteUrl}</a></p>
              </body>
            </html>
            """;

        await notificationQueue.EnqueueAsync(
            new NotificationMessage(
                Type: "Email",
                Recipient: invite.Email,
                Subject: "Convite Rolvix — defina sua senha",
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
