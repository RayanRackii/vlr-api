using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Notifications;

public static class NotificationTemplateReconciler
{
    public static async Task ReconcileAsync(
        AppDbContext dbContext,
        IEnumerable<NotificationTemplate> seeds,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.NotificationTemplates.ToListAsync(cancellationToken);
        var byKey = existing.ToDictionary(
            t => Key(t.EventType, t.Channel, t.Language),
            StringComparer.Ordinal);

        foreach (var local in dbContext.NotificationTemplates.Local)
        {
            byKey.TryAdd(Key(local.EventType, local.Channel, local.Language), local);
        }

        foreach (var seed in seeds)
        {
            var key = Key(seed.EventType, seed.Channel, seed.Language);
            if (byKey.TryGetValue(key, out var current))
            {
                var changed = false;
                if (!string.Equals(
                        current.WhatsAppTemplateName,
                        seed.WhatsAppTemplateName,
                        StringComparison.Ordinal))
                {
                    current.WhatsAppTemplateName = seed.WhatsAppTemplateName;
                    changed = true;
                }

                if (changed)
                {
                    current.Touch();
                }

                continue;
            }

            dbContext.NotificationTemplates.Add(seed);
            byKey[key] = seed;
        }
    }

    private static string Key(
        string eventType,
        Platform.Core.Domain.Enums.NotificationChannel channel,
        string language) =>
        $"{eventType}|{channel}|{language}";
}
