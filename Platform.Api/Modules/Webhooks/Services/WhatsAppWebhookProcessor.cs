using System.Text.Json;
using Platform.Api.Notifications;

namespace Platform.Api.Modules.Webhooks.Services;

/// <summary>
/// Processa payloads de webhook do WhatsApp (Meta Cloud API): atualizações de
/// status de entrega e mensagens recebidas. Hoje apenas registra em log de
/// forma estruturada; a persistência de status por mensagem virá depois.
/// </summary>
public sealed class WhatsAppWebhookProcessor(ILogger<WhatsAppWebhookProcessor> logger)
    : IWhatsAppWebhookProcessor
{
    public void Process(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);

            if (!document.RootElement.TryGetProperty("entry", out var entries))
            {
                logger.LogWarning("WhatsApp webhook payload without 'entry'.");
                return;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes))
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value))
                    {
                        continue;
                    }

                    LogStatuses(value);
                    LogInboundMessages(value);
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "WhatsApp webhook payload is not valid JSON.");
        }
    }

    private void LogStatuses(JsonElement value)
    {
        if (!value.TryGetProperty("statuses", out var statuses))
        {
            return;
        }

        foreach (var status in statuses.EnumerateArray())
        {
            var messageId = GetString(status, "id");
            var state = GetString(status, "status");
            var recipient = GetString(status, "recipient_id");

            if (status.TryGetProperty("errors", out var errors))
            {
                logger.LogWarning(
                    "WhatsApp message {MessageId} to phone ending {Last4} failed with status '{Status}'. ErrorCount present.",
                    messageId,
                    PhoneLogMask.Last4(recipient),
                    state);
                continue;
            }

            logger.LogInformation(
                "WhatsApp message {MessageId} to phone ending {Last4} updated to status '{Status}'.",
                messageId,
                PhoneLogMask.Last4(recipient),
                state);
        }
    }

    private void LogInboundMessages(JsonElement value)
    {
        if (!value.TryGetProperty("messages", out var messages))
        {
            return;
        }

        foreach (var message in messages.EnumerateArray())
        {
            var from = GetString(message, "from");
            var type = GetString(message, "type");

            logger.LogInformation(
                "WhatsApp inbound message from phone ending {Last4} (type: {Type}).",
                PhoneLogMask.Last4(from),
                type);
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }
}
