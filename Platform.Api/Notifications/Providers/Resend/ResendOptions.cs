namespace Platform.Api.Notifications.Providers.Resend;

public sealed class ResendOptions
{
    public const string SectionName = "Resend";

    public string ApiUrl { get; set; } = "https://api.resend.com";
    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Rolvix";
}
