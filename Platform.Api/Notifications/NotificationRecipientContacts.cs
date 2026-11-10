using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Notifications;

internal static class NotificationRecipientContacts
{
    public static bool AllowsExternalSend(string eventType, NotificationChannel channel)
    {
        if (channel is not (NotificationChannel.Email or NotificationChannel.WhatsApp))
        {
            return false;
        }

        if (!NotificationChannelCapability.TryGetEvent(eventType, out var capability))
        {
            return true;
        }

        return capability.Channels.Any(entry => entry.Channel == channel);
    }

    public static async Task<(string? Email, string? Phone)> ResolveForResendAsync(
        AppDbContext db,
        NotificationDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (delivery.RecipientId is not Guid recipientId || recipientId == Guid.Empty)
        {
            return (delivery.RecipientEmail, delivery.RecipientPhone);
        }

        if (delivery.RecipientKind == NotificationRecipientKind.Customer)
        {
            var row = await db.Customers
                .AsNoTracking()
                .Where(customer => customer.Id == recipientId && customer.TenantId == delivery.TenantId)
                .Select(customer => new { customer.Email, customer.Phone })
                .FirstOrDefaultAsync(cancellationToken);
            return (Clean(row?.Email), Clean(row?.Phone));
        }

        if (delivery.RecipientKind == NotificationRecipientKind.B2BUser)
        {
            var row = await db.Users
                .AsNoTracking()
                .Where(user => user.Id == recipientId && user.TenantId == delivery.TenantId)
                .Select(user => new { user.Email, user.Phone })
                .FirstOrDefaultAsync(cancellationToken);
            return (Clean(row?.Email), Clean(row?.Phone));
        }

        return (null, null);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
