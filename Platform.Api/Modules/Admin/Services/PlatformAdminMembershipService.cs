using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Admin.Services;

public interface IPlatformAdminMembershipService
{
    Task ProvisionPlatformAdminsAsync(Guid tenantId, CancellationToken cancellationToken);

    Task EnsureMembershipAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task EnterTenantAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task ExitTenantAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}

public sealed class PlatformAdminMembershipService(
    AppDbContext dbContext,
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    IOptions<PlatformAdminOptions> platformAdminOptions,
    ITenantAccessBootstrapper tenantAccessBootstrapper,
    ILogger<PlatformAdminMembershipService> logger) : IPlatformAdminMembershipService
{
    public async Task ProvisionPlatformAdminsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var emails = platformAdminOptions.Value.Emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var email in emails)
        {
            try
            {
                var authId = await ResolveOrCreateAuthUserIdAsync(email, cancellationToken);

                await EnsureUserMembershipAsync(
                    tenantId,
                    authId,
                    fullName: DeriveFullName(email),
                    email,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to provision platform admin {Email} into tenant {TenantId}.",
                    email,
                    tenantId);
            }
        }
    }

    public async Task EnsureMembershipAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var email = ResolveEmail(principal)
            ?? throw new UnauthorizedAccessException("Authenticated email is required.");

        var authId = await ResolveOrCreateAuthUserIdAsync(
            email.Trim().ToLowerInvariant(),
            cancellationToken);

        await EnsureUserMembershipAsync(
            tenantId,
            authId,
            fullName: DeriveFullName(email),
            email.Trim().ToLowerInvariant(),
            cancellationToken);
    }

    public async Task EnterTenantAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var tenantExists = await dbContext.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.IsActive, cancellationToken);

        if (!tenantExists)
        {
            throw new KeyNotFoundException("Tenant not found or inactive.");
        }

        var email = ResolveEmail(principal)
            ?? throw new UnauthorizedAccessException("Authenticated email is required.");

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var authId = await ResolveOrCreateAuthUserIdAsync(normalizedEmail, cancellationToken);
        var jwtSub = ResolveAuthId(principal);

        var sessionMatchesAuth =
            !string.IsNullOrWhiteSpace(jwtSub)
            && string.Equals(jwtSub, authId, StringComparison.OrdinalIgnoreCase)
            && await supabaseAuthAdminClient.UserExistsAsync(jwtSub, cancellationToken);

        await EnsureUserMembershipAsync(
            tenantId,
            authId,
            fullName: DeriveFullName(normalizedEmail),
            normalizedEmail,
            cancellationToken);

        await supabaseAuthAdminClient.UpdateUserAppMetadataAsync(
            authId,
            tenantId,
            cancellationToken);

        if (!sessionMatchesAuth)
        {
            throw new InvalidOperationException(
                "Sua sessão Auth está desatualizada (conta recriada ou JWT órfão após exclusão de tenant). "
                + "Faça logout, use \"Esqueci a senha\" com este e-mail e entre de novo; depois tente Abrir ambiente.");
        }
    }

    public async Task ExitTenantAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var email = ResolveEmail(principal);
        string? authId = null;

        if (!string.IsNullOrWhiteSpace(email))
        {
            authId = await supabaseAuthAdminClient.FindUserIdByEmailAsync(
                email,
                cancellationToken);
        }

        authId ??= ResolveAuthId(principal)
            ?? throw new UnauthorizedAccessException("Authenticated user identifier is missing.");

        if (!await supabaseAuthAdminClient.UserExistsAsync(authId, cancellationToken))
        {
            throw new InvalidOperationException(
                "Sua sessão Auth está desatualizada. Faça logout e entre de novo antes de voltar à plataforma.");
        }

        await supabaseAuthAdminClient.ClearUserTenantAppMetadataAsync(
            authId,
            cancellationToken);
    }

    private async Task<string> ResolveOrCreateAuthUserIdAsync(
        string normalizedEmail,
        CancellationToken cancellationToken)
    {
        var authId = await supabaseAuthAdminClient.FindUserIdByEmailAsync(
            normalizedEmail,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(authId))
        {
            return authId;
        }

        try
        {
            var createdId = await supabaseAuthAdminClient.CreateUserAsync(
                normalizedEmail,
                GenerateTemporaryPassword(),
                cancellationToken);

            logger.LogWarning(
                "Recreated missing Supabase Auth user for platform admin {Email} ({AuthId}). They must reset password before signing in.",
                normalizedEmail,
                createdId);

            return createdId;
        }
        catch (SupabaseAuthAdminException ex)
        {
            // Race / parse miss: user may already exist — retry lookup once.
            authId = await supabaseAuthAdminClient.FindUserIdByEmailAsync(
                normalizedEmail,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(authId))
            {
                return authId;
            }

            throw new InvalidOperationException(
                $"Could not resolve or recreate Supabase Auth for {normalizedEmail}.",
                ex);
        }
    }

    private async Task EnsureUserMembershipAsync(
        Guid tenantId,
        string supabaseAuthId,
        string fullName,
        string email,
        CancellationToken cancellationToken)
    {
        await tenantAccessBootstrapper.EnsureAsync(tenantId, cancellationToken);

        var existing = await dbContext.Users
            .IgnoreQueryFilters()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(
                u => u.TenantId == tenantId && u.SupabaseAuthId == supabaseAuthId,
                cancellationToken);

        var adminRole = await EnsureAdminRoleAsync(tenantId, cancellationToken);

        if (existing is null)
        {
            var user = new User(tenantId, supabaseAuthId, fullName, email);
            dbContext.Users.Add(user);
            dbContext.UserRoles.Add(new UserRole(user.Id, adminRole.Id));
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!existing.IsActive)
        {
            existing.Activate();
        }

        var hasAdmin = existing.UserRoles.Any(ur =>
            ur.Role.Name.Equals(SystemRoles.Admin, StringComparison.OrdinalIgnoreCase));

        if (!hasAdmin)
        {
            dbContext.UserRoles.Add(new UserRole(existing.Id, adminRole.Id));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Role> EnsureAdminRoleAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.Roles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId
                    && r.Name.ToLower() == SystemRoles.Admin.ToLower(),
                cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var role = new Role(
            tenantId,
            SystemRoles.Admin,
            "Admin (system)",
            isSystemRole: true);

        dbContext.Roles.Add(role);
        await dbContext.SaveChangesAsync(cancellationToken);
        return role;
    }

    private static string DeriveFullName(string email)
    {
        var local = email.Split('@')[0]?.Trim();
        return string.IsNullOrWhiteSpace(local) ? email : local;
    }

    private static string GenerateTemporaryPassword()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string? ResolveEmail(ClaimsPrincipal principal) =>
        principal.FindFirst("email")?.Value
        ?? principal.FindFirst(ClaimTypes.Email)?.Value
        ?? principal.Identity?.Name;

    private static string? ResolveAuthId(ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
