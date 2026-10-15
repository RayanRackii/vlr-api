using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Notifications;

internal static class StaffInviteRoleNames
{
    public static async Task<IReadOnlyList<string>> LoadAsync(
        AppDbContext dbContext,
        Guid tenantId,
        Guid inviteId,
        string? fallbackRoleName,
        CancellationToken cancellationToken)
    {
        var names = await dbContext.UserInviteRoles
            .AsNoTracking()
            .Where(link => link.InviteId == inviteId)
            .Join(
                dbContext.Roles.AsNoTracking().Where(role => role.TenantId == tenantId),
                link => link.RoleId,
                role => role.Id,
                (_, role) => role.Name)
            .ToListAsync(cancellationToken);

        var distinct = new List<string>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var trimmed = name.Trim();
            if (distinct.Any(existing => existing.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            distinct.Add(trimmed);
        }

        if (distinct.Count == 0 && !string.IsNullOrWhiteSpace(fallbackRoleName))
        {
            return [fallbackRoleName.Trim()];
        }

        return distinct;
    }
}
