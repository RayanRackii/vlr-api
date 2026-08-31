namespace Platform.Api.Modules.CustomerAuth.PhoneVerification;

public sealed class TwilioVerifyOptions
{
    public const string SectionName = "Twilio";

    public string AccountSid { get; set; } = string.Empty;

    public string ApiKeySid { get; set; } = string.Empty;

    public string ApiKeySecret { get; set; } = string.Empty;

    public string VerifyServiceSid { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = "https://verify.twilio.com/v2/";
}
