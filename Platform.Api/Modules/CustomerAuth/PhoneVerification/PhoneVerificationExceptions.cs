namespace Platform.Api.Modules.CustomerAuth.PhoneVerification;

public sealed class PhoneVerificationProviderException : Exception
{
    public PhoneVerificationProviderException(string message)
        : base(message)
    {
    }

    public PhoneVerificationProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class PhoneVerificationRateLimitedException : Exception
{
    public PhoneVerificationRateLimitedException(string message)
        : base(message)
    {
    }
}

public sealed class PhoneVerificationInvalidException : Exception
{
    public PhoneVerificationInvalidException(string message)
        : base(message)
    {
    }
}
