namespace Platform.Api.Modules.CustomerAuth.PhoneVerification;

public interface IPhoneVerificationClient
{
    Task StartVerificationAsync(string phoneE164, CancellationToken cancellationToken);

    Task CheckVerificationAsync(string phoneE164, string code, CancellationToken cancellationToken);
}
