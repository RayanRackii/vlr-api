using Platform.Api.Modules.CustomerAuth.PhoneVerification;

namespace Platform.Api.Tests.Fakes;

public sealed class FakePhoneVerificationClient : IPhoneVerificationClient
{
    public string ApprovedCode { get; set; } = "123456";

    public List<string> StartedPhones { get; } = [];

    public bool StartThrowsProvider { get; set; }

    public bool StartThrowsRateLimit { get; set; }

    public bool CheckThrowsProvider { get; set; }

    public bool CheckThrowsRateLimit { get; set; }

    public Task StartVerificationAsync(string phoneE164, CancellationToken cancellationToken)
    {
        StartedPhones.Add(phoneE164);

        if (StartThrowsProvider)
        {
            throw new PhoneVerificationProviderException(
                TwilioVerifyPhoneVerificationClient.ProviderUnavailableMessage);
        }

        if (StartThrowsRateLimit)
        {
            throw new PhoneVerificationRateLimitedException(
                TwilioVerifyPhoneVerificationClient.RateLimitedMessage);
        }

        return Task.CompletedTask;
    }

    public Task CheckVerificationAsync(
        string phoneE164,
        string code,
        CancellationToken cancellationToken)
    {
        if (CheckThrowsProvider)
        {
            throw new PhoneVerificationProviderException(
                TwilioVerifyPhoneVerificationClient.ProviderUnavailableMessage);
        }

        if (CheckThrowsRateLimit)
        {
            throw new PhoneVerificationRateLimitedException(
                TwilioVerifyPhoneVerificationClient.RateLimitedMessage);
        }

        if (!string.Equals(code, ApprovedCode, StringComparison.Ordinal))
        {
            throw new PhoneVerificationInvalidException(
                TwilioVerifyPhoneVerificationClient.InvalidOrExpiredMessage);
        }

        return Task.CompletedTask;
    }
}
