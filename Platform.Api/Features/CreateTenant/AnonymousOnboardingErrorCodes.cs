namespace Platform.Api.Features.CreateTenant;

public static class AnonymousOnboardingErrorCodes
{
    public const string TrialRequired = "ANONYMOUS_TRIAL_REQUIRED";

    public const string RateLimited = "ANONYMOUS_TRIAL_RATE_LIMITED";

    public const string SignupRejected = "SIGNUP_REJECTED";

    public const string SignupUnavailable = "SIGNUP_UNAVAILABLE";
}
