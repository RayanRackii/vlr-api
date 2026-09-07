namespace Platform.Api.Notifications;

public sealed class WhatsAppSendException : Exception
{
    public WhatsAppSendException(string message, bool isTransient, int? providerCode = null)
        : base(message)
    {
        IsTransient = isTransient;
        ProviderCode = providerCode;
    }

    public bool IsTransient { get; }

    public int? ProviderCode { get; }
}
