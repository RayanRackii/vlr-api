namespace Platform.Api.Modules.Webhooks.Services;

public interface IWhatsAppWebhookProcessor
{
    void Process(string payload);
}
