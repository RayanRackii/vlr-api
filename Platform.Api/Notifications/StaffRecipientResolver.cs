using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Notifications;

/// <summary>
/// An active B2B staff member who should receive an in-app notification for an event.
/// </summary>
public sealed record StaffRecipient(Guid UserId, string FullName, string? Email);

public interface IStaffRecipientResolver
{
    /// <summary>
    /// Resolves the active B2B users in <paramref name="tenantId"/> that currently hold
    /// <paramref name="permissionKey"/>. Shared by every notifying module so the "who is staff for
    /// this event" rule lives in one place instead of being copied per publisher.
    /// </summary>
    Task<IReadOnlyList<StaffRecipient>> ResolveAsync(
        Guid tenantId,
        string permissionKey,
        CancellationToken cancellationToken = default);
}

public sealed class StaffRecipientResolver(
    AppDbContext dbContext,
    IPermissionResolver permissionResolver) : IStaffRecipientResolver
{
    public async Task<IReadOnlyList<StaffRecipient>> ResolveAsync(
        Guid tenantId,
        string permissionKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(permissionKey))
        {
            return [];
        }

        // Single query for the tenant's active staff, then one permission evaluation each. The
        // resolver caches role/permission reads per request, so the per-user cost is bounded.
        var users = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.TenantId == tenantId)
            .Select(user => new { user.Id, user.FullName, user.Email })
            .ToListAsync(cancellationToken);

        var recipients = new List<StaffRecipient>(users.Count);
        foreach (var user in users)
        {
            if (await permissionResolver.HasPermissionAsync(
                    tenantId,
                    user.Id,
                    permissionKey,
                    cancellationToken))
            {
                recipients.Add(new StaffRecipient(user.Id, user.FullName, user.Email));
            }
        }

        return recipients;
    }
}
