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
            return new CurrentUserResponse(null, email, email, ApplicationRoles.SuperAdmin);
        }

        _ = tenantProvider.TenantId
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

        return new CurrentUserResponse(user.Id, user.FullName, user.Email, role);
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
}
