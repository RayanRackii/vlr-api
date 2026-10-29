using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Notifications;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Notifications.Services;

public interface ICustomerNotificationsService
{
    Task<MeNotificationSummaryResponse> GetSummaryAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<MeNotificationsListResponse> ListAsync(ClaimsPrincipal principal, string? cursor, bool unreadOnly, int limit, string? language, CancellationToken cancellationToken);
    Task<int?> MarkReadAsync(ClaimsPrincipal principal, Guid id, CancellationToken cancellationToken);
    Task<int> MarkAllReadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

public sealed class CustomerNotificationsService(AppDbContext dbContext) : ICustomerNotificationsService
{
    // The in-memory test provider cannot perform atomic ExecuteUpdate operations.
    private static readonly SemaphoreSlim InMemoryReadGate = new(1, 1);

    public async Task<MeNotificationSummaryResponse> GetSummaryAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var rows = await Inbox(principal).AsNoTracking().Where(d => d.ReadAt == null)
            .GroupBy(d => d.Notification.EventType)
            .Select(group => new
            {
                EventType = group.Key,
                Count = group.Count(),
                Latest = group.OrderByDescending(d => d.Notification.CreatedAt).ThenByDescending(d => d.Id)
                    .Select(d => new { d.Id, d.Notification.CreatedAt, d.Notification.Payload }).First(),
            })
            .ToListAsync(cancellationToken);
        var groups = rows.OrderByDescending(r => r.Latest.CreatedAt).ThenByDescending(r => r.Latest.Id).Select(row =>
        {
            NotificationChannelCapability.TryGetEvent(row.EventType, out var capability);
            return new MeNotificationGroup(row.EventType, row.Latest.Payload.GetValueOrDefault("module") ?? capability?.ModuleKey ?? string.Empty,
                row.Count, row.Latest.CreatedAt, row.Latest.Payload.GetValueOrDefault("targetRoute"));
        }).ToList();
        return new MeNotificationSummaryResponse(rows.Sum(r => r.Count), groups);
    }

    public async Task<MeNotificationsListResponse> ListAsync(ClaimsPrincipal principal, string? cursor,
        bool unreadOnly, int limit, string? language, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentException("Limit must be between 1 and 50.");
        }

        var query = Inbox(principal).AsNoTracking();
        if (unreadOnly)
        {
            query = query.Where(d => d.ReadAt == null);
        }

        if (cursor is not null)
        {
            var (createdAt, id) = DecodeCursor(cursor);
            query = query.Where(d => d.Notification.CreatedAt < createdAt
                || (d.Notification.CreatedAt == createdAt && d.Id.CompareTo(id) < 0));
        }

        var rows = await query.OrderByDescending(d => d.Notification.CreatedAt).ThenByDescending(d => d.Id)
            .Select(d => new
            {
                d.Id, d.ReadAt, d.Notification.EventType, d.Notification.AggregateType,
                d.Notification.AggregateId, d.Notification.CreatedAt, d.Notification.Payload,
            }).Take(limit + 1).ToListAsync(cancellationToken);
        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            nextCursor = EncodeCursor(last.CreatedAt, last.Id);
            rows.RemoveAt(limit);
        }

        var events = rows.Select(r => r.EventType).Distinct().ToList();
        var templates = await dbContext.NotificationTemplates.AsNoTracking()
            .Where(t => t.Channel == NotificationChannel.InApp && t.IsActive && events.Contains(t.EventType))
            .ToListAsync(cancellationToken);
        var preferred = language?.Split(',')[0].Split(';')[0].Trim();
        var titles = templates.GroupBy(t => t.EventType).ToDictionary(g => g.Key, g =>
            (g.FirstOrDefault(t => string.Equals(t.Language, preferred, StringComparison.OrdinalIgnoreCase))
                ?? g.FirstOrDefault(t => t.Language == "pt-BR") ?? g.First()).BodyTemplate);
        var items = rows.Select(row =>
        {
            NotificationChannelCapability.TryGetEvent(row.EventType, out var capability);
            var title = titles.GetValueOrDefault(row.EventType) ?? string.Empty;
            foreach (var (key, value) in row.Payload)
            {
                title = title.Replace("{{" + key + "}}", value ?? string.Empty, StringComparison.Ordinal);
            }

            return new MeNotificationItem(row.Id, row.EventType, row.Payload.GetValueOrDefault("module") ?? capability?.ModuleKey ?? string.Empty,
                row.AggregateType, row.AggregateId, title, row.CreatedAt, row.ReadAt,
                row.Payload.GetValueOrDefault("targetRoute"));
        }).ToList();
        return new MeNotificationsListResponse(items, nextCursor);
    }

    public async Task<int?> MarkReadAsync(ClaimsPrincipal principal, Guid id, CancellationToken cancellationToken)
    {
        var query = Inbox(principal).Where(d => d.Id == id);
        var updated = await MarkUnreadAsync(query, cancellationToken);
        return updated > 0 || await query.AnyAsync(cancellationToken) ? updated : null;
    }

    public Task<int> MarkAllReadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        MarkUnreadAsync(Inbox(principal), cancellationToken);

    private async Task<int> MarkUnreadAsync(IQueryable<NotificationDelivery> query, CancellationToken cancellationToken)
    {
        var unread = query.Where(d => d.ReadAt == null);
        if (dbContext.Database.IsRelational())
        {
            var now = DateTimeOffset.UtcNow;
            return await unread.ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.ReadAt, now)
                .SetProperty(d => d.UpdatedAt, now), cancellationToken);
        }

        await InMemoryReadGate.WaitAsync(cancellationToken);
        try
        {
            var deliveries = await unread.ToListAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var updated = 0;
            foreach (var delivery in deliveries)
            {
                if (delivery.ReadAt is null)
                {
                    delivery.MarkRead(now);
                    updated++;
                }
            }

            if (updated > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return updated;
        }
        finally
        {
            InMemoryReadGate.Release();
        }
    }

    private IQueryable<NotificationDelivery> Inbox(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || !principal.IsInRole(AuthRoles.Customer)
            || !Guid.TryParse(principal.FindFirst(CustomerClaimTypes.CustomerId)?.Value, out var customerId)
            || !Guid.TryParse(principal.FindFirst(CustomerClaimTypes.TenantId)?.Value, out var tenantId)
            || customerId == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("A valid customer and tenant identity is required.");
        }

        return dbContext.NotificationDeliveries.Where(d => d.Channel == NotificationChannel.InApp
            && d.RecipientKind == NotificationRecipientKind.Customer && d.RecipientId == customerId
            && d.TenantId == tenantId && d.Notification.TenantId == tenantId);
    }

    private static string EncodeCursor(DateTimeOffset createdAt, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{id:N}"));

    private static (DateTimeOffset CreatedAt, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':', 2);
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks >= DateTimeOffset.MinValue.Ticks && ticks <= DateTimeOffset.MaxValue.Ticks
                && Guid.TryParseExact(parts[1], "N", out var id))
            {
                return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
            }
        }
        catch (FormatException)
        {
            // The controller maps an invalid cursor to the same 400 as other invalid list inputs.
        }

        throw new ArgumentException("Invalid notification cursor.");
    }
}
