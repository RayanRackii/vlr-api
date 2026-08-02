using System.Text.Json;

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
                logger.LogWarning("WhatsApp webhook payload without 'entry': {Payload}", payload);
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
            logger.LogWarning(ex, "WhatsApp webhook payload is not valid JSON: {Payload}", payload);
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
                    "WhatsApp message {MessageId} to {Recipient} failed with status '{Status}'. Errors: {Errors}",
                    messageId,
                    recipient,
                    state,
                    errors.GetRawText());
                continue;
            }

            logger.LogInformation(
                "WhatsApp message {MessageId} to {Recipient} updated to status '{Status}'.",
                messageId,
                recipient,
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
            var text = message.TryGetProperty("text", out var textElement)
                ? GetString(textElement, "body")
                : null;

            logger.LogInformation(
                "WhatsApp inbound message from {From} (type: {Type}): {Text}",
                from,
                type,
                text ?? "<non-text>");
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetString()
            : null;
    }
}
