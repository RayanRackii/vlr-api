using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Admin.Services;

public interface IPlatformUserAdminService
{
    Task<IReadOnlyList<PlatformUserResponseDto>> ListAsync(
        string? name,
        Guid? tenantId,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid userId,
        string? actingSupabaseAuthId,
        CancellationToken cancellationToken);
}

public sealed class PlatformUserAdminService(
    AppDbContext dbContext,
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    IOptions<PlatformAdminOptions> platformAdminOptions,
    ILogger<PlatformUserAdminService> logger) : IPlatformUserAdminService
{
    public async Task<IReadOnlyList<PlatformUserResponseDto>> ListAsync(
        string? name,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var query =
            from user in dbContext.Users.AsNoTracking()
            join tenant in dbContext.Tenants.AsNoTracking()
                on user.TenantId equals tenant.Id
            select new { user, tenant };

        if (tenantId is Guid filterTenantId)
        {
            query = query.Where(row => row.user.TenantId == filterTenantId);
        }

        var trimmedName = name?.Trim();
        if (!string.IsNullOrWhiteSpace(trimmedName))
        {
            var pattern = $"%{EscapeLikePattern(trimmedName)}%";
            query = query.Where(row =>
                EF.Functions.ILike(row.user.FullName, pattern));
        }

        var rows = await query
            .OrderBy(row => row.user.FullName)
            .ThenBy(row => row.tenant.LegalName)
            .Select(row => new
            {
                row.user.Id,
                row.user.FullName,
                row.user.Email,
                row.user.IsActive,
                row.user.TenantId,
                TenantLegalName = row.tenant.LegalName,
                TenantSubdomain = row.tenant.Subdomain,
                row.user.CreatedAt,
                Roles = row.user.UserRoles
                    .Select(ur => ur.Role.Name)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new PlatformUserResponseDto(
                row.Id,
                row.FullName,
                row.Email,
                row.IsActive,
                row.TenantId,
                row.TenantLegalName,
                row.TenantSubdomain,
                row.Roles,
                row.CreatedAt))
            .ToList();
    }

    public async Task DeleteAsync(
        Guid userId,
        string? actingSupabaseAuthId,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            throw new KeyNotFoundException("User not found.");
        }

        if (!string.IsNullOrWhiteSpace(actingSupabaseAuthId)
            && string.Equals(
                user.SupabaseAuthId,
                actingSupabaseAuthId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("You cannot delete your own user account.");
        }

        var supabaseAuthId = user.SupabaseAuthId;
        var email = user.Email.Trim().ToLowerInvariant();

        var isPlatformAdmin = platformAdminOptions.Value.Emails.Any(e =>
            string.Equals(e.Trim(), email, StringComparison.OrdinalIgnoreCase));

        var stillUsedElsewhere = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                u => u.Id != userId && u.SupabaseAuthId == supabaseAuthId,
                cancellationToken);

        dbContext.Users.Remove(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (isPlatformAdmin || stillUsedElsewhere)
        {
            logger.LogInformation(
                "Platform user {UserId} removed from database; kept Supabase Auth {SupabaseAuthId} (platformAdmin={IsPlatformAdmin}, usedElsewhere={UsedElsewhere}).",
                userId,
                supabaseAuthId,
                isPlatformAdmin,
                stillUsedElsewhere);
            return;
        }

        try
        {
            await supabaseAuthAdminClient.DeleteUserAsync(supabaseAuthId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Platform user {UserId} removed from database, but Supabase auth delete failed for {SupabaseAuthId}.",
                userId,
                supabaseAuthId);
        }
    }

    private static string EscapeLikePattern(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
