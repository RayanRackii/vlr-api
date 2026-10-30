using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Notifications;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Notifications.Services;

public interface IMeNotificationsService
{
    Task<MeNotificationSummaryResponse> GetSummaryAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<MeNotificationsListResponse> ListAsync(
        ClaimsPrincipal principal,
        string? cursor,
        bool unreadOnly,
        int limit,
        string? language,
        CancellationToken cancellationToken);

    Task<int> MarkReadAsync(
        ClaimsPrincipal principal,
        MarkNotificationsReadRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The "bell" inbox for B2B staff. Reads only the caller's own in-app deliveries in the caller's
/// tenant, and re-checks the per-event staff permission at read time so a revoked permission hides
/// the matching groups/items and drops them from the unread count. All module-specific knowledge
/// (which permission, which module, which web route) comes from <see cref="NotificationChannelCapability"/>.
/// </summary>
public sealed class MeNotificationsService(
    AppDbContext dbContext,
    IRbacActorAccessor actorAccessor,
    IPermissionResolver permissionResolver) : IMeNotificationsService
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    private const string DefaultLanguage = "pt-BR";

    public async Task<MeNotificationSummaryResponse> GetSummaryAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(principal, cancellationToken);
        if (context is null || context.AllowedEventTypes.Count == 0)
        {
            return new MeNotificationSummaryResponse(0, []);
        }

        var allowed = context.AllowedEventTypes;
        var grouped = await (
                from d in dbContext.NotificationDeliveries.AsNoTracking()
                where d.Channel == NotificationChannel.InApp
                    && d.RecipientKind == NotificationRecipientKind.B2BUser
                    && d.RecipientId == context.UserId
                    && d.TenantId == context.TenantId
                    && d.ReadAt == null
                join n in dbContext.Notifications.AsNoTracking() on d.NotificationId equals n.Id
                where allowed.Contains(n.EventType)
                group d by n.EventType into g
                select new { EventType = g.Key, Count = g.Count(), LatestAt = g.Max(x => x.CreatedAt) })
            .ToListAsync(cancellationToken);

        var groups = grouped
            .OrderByDescending(g => g.LatestAt)
            .Select(g =>
            {
                NotificationChannelCapability.TryGetEvent(g.EventType, out var capability);
                return new MeNotificationGroup(
                    g.EventType,
                    capability?.ModuleKey ?? string.Empty,
                    g.Count,
                    g.LatestAt,
                    capability?.TargetRoute);
            })
            .ToList();

        return new MeNotificationSummaryResponse(groups.Sum(g => g.Count), groups);
    }

    public async Task<MeNotificationsListResponse> ListAsync(
        ClaimsPrincipal principal,
        string? cursor,
        bool unreadOnly,
        int limit,
        string? language,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);

        var context = await ResolveContextAsync(principal, cancellationToken);
        if (context is null || context.AllowedEventTypes.Count == 0)
        {
            return new MeNotificationsListResponse([], null);
        }

        var allowed = context.AllowedEventTypes;
        var query =
            from d in dbContext.NotificationDeliveries.AsNoTracking()
            where d.Channel == NotificationChannel.InApp
                && d.RecipientKind == NotificationRecipientKind.B2BUser
                && d.RecipientId == context.UserId
                && d.TenantId == context.TenantId
            join n in dbContext.Notifications.AsNoTracking() on d.NotificationId equals n.Id
            where allowed.Contains(n.EventType)
            select new Row
            {
                Id = d.Id,
                EventType = n.EventType,
                AggregateType = n.AggregateType,
                AggregateId = n.AggregateId,
                CreatedAt = d.CreatedAt,
                ReadAt = d.ReadAt,
                Payload = n.Payload,
            };

        if (unreadOnly)
        {
            query = query.Where(r => r.ReadAt == null);
        }

        if (TryDecodeCursor(cursor, out var createdAt, out var id))
        {
            // Keyset page over the stable (CreatedAt desc, Id desc) order.
            query = query.Where(r =>
                r.CreatedAt < createdAt || (r.CreatedAt == createdAt && r.Id.CompareTo(id) < 0));
        }

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            nextCursor = EncodeCursor(last.CreatedAt, last.Id);
            rows = rows.Take(limit).ToList();
        }

        var titles = await LoadTitlesAsync(
            rows.Select(r => r.EventType).Distinct().ToList(),
            language,
            cancellationToken);

        var items = rows.Select(r =>
        {
            NotificationChannelCapability.TryGetEvent(r.EventType, out var capability);
            var body = titles.GetValueOrDefault(r.EventType) ?? string.Empty;
            return new MeNotificationItem(
                r.Id,
                r.EventType,
                capability?.ModuleKey ?? string.Empty,
                r.AggregateType,
                r.AggregateId,
                Render(body, r.Payload),
                r.CreatedAt,
                r.ReadAt,
                capability?.TargetRoute);
        }).ToList();

        return new MeNotificationsListResponse(items, nextCursor);
    }

    public async Task<int> MarkReadAsync(
        ClaimsPrincipal principal,
        MarkNotificationsReadRequest request,
        CancellationToken cancellationToken)
    {
        var context = await ResolveContextAsync(principal, cancellationToken);
        if (context is null || context.AllowedEventTypes.Count == 0)
        {
            return 0;
        }

        var allowed = context.AllowedEventTypes;
        var query =
            from d in dbContext.NotificationDeliveries.AsNoTracking()
            where d.Channel == NotificationChannel.InApp
                && d.RecipientKind == NotificationRecipientKind.B2BUser
                && d.RecipientId == context.UserId
                && d.TenantId == context.TenantId
                && d.ReadAt == null
            join n in dbContext.Notifications.AsNoTracking() on d.NotificationId equals n.Id
            where allowed.Contains(n.EventType)
            select new { d.Id, n.EventType };

        if (request.Ids is { Count: > 0 } ids)
        {
            var idSet = ids.ToHashSet();
            query = query.Where(x => idSet.Contains(x.Id));
        }
        else if (!string.IsNullOrWhiteSpace(request.EventType))
        {
            query = query.Where(x => x.EventType == request.EventType);
        }
        // else: mark-all — no further restriction beyond the caller/permission filters above.

        var markableIds = await query.Select(x => x.Id).ToListAsync(cancellationToken);
        if (markableIds.Count == 0)
        {
            return 0;
        }

        var entities = await dbContext.NotificationDeliveries
            .Where(d => markableIds.Contains(d.Id) && d.ReadAt == null)
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var delivery in entities)
        {
            delivery.MarkRead(now);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return entities.Count;
    }

    private async Task<InboxContext?> ResolveContextAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var actor = await actorAccessor.GetAsync(principal, cancellationToken);
        if (actor.UserId is not Guid userId)
        {
            return null;
        }

        var effective = await permissionResolver.GetEffectivePermissionsAsync(
            actor.TenantId,
            userId,
            cancellationToken);

        var allowed = NotificationChannelCapability.Events
            .Where(e => e.StaffPermission is { Length: > 0 } permission && effective.Contains(permission))
            .Select(e => e.EventType)
            .ToHashSet(StringComparer.Ordinal);

        return new InboxContext(actor.TenantId, userId, allowed);
    }

    private async Task<Dictionary<string, string>> LoadTitlesAsync(
        IReadOnlyList<string> eventTypes,
        string? language,
        CancellationToken cancellationToken)
    {
        if (eventTypes.Count == 0)
        {
            return [];
        }

        var templates = await dbContext.NotificationTemplates
            .AsNoTracking()
            .Where(t => t.Channel == NotificationChannel.InApp
                && t.IsActive
                && eventTypes.Contains(t.EventType))
            .ToListAsync(cancellationToken);

        var preferred = NormalizeLanguage(language);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in templates.GroupBy(t => t.EventType, StringComparer.Ordinal))
        {
            // Prefer the caller's language, then pt-BR, then whatever active template exists.
            var pick = group.FirstOrDefault(t => string.Equals(t.Language, preferred, StringComparison.OrdinalIgnoreCase))
                ?? group.FirstOrDefault(t => string.Equals(t.Language, DefaultLanguage, StringComparison.OrdinalIgnoreCase))
                ?? group.First();
            result[group.Key] = pick.BodyTemplate;
        }

        return result;
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return DefaultLanguage;
        }

        // Accept-Language may carry quality values / multiple entries; take the first token.
        var first = language.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return DefaultLanguage;
        }

        return first.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
    }

    private static string Render(string template, IReadOnlyDictionary<string, string?> payload)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var result = template;
        foreach (var (key, value) in payload)
        {
            result = result.Replace("{{" + key + "}}", value ?? string.Empty, StringComparison.Ordinal);
        }

        return result;
    }

    private static string EncodeCursor(DateTimeOffset createdAt, Guid id)
    {
        var raw = $"{createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static bool TryDecodeCursor(string? cursor, out DateTimeOffset createdAt, out Guid id)
    {
        createdAt = default;
        id = default;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return false;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split(':', 2);
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParseExact(parts[1], "N", out id))
            {
                return false;
            }

            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string EventType { get; init; } = string.Empty;
        public string AggregateType { get; init; } = string.Empty;
        public Guid AggregateId { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? ReadAt { get; init; }
        public Dictionary<string, string?> Payload { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record InboxContext(Guid TenantId, Guid UserId, IReadOnlySet<string> AllowedEventTypes);
}
