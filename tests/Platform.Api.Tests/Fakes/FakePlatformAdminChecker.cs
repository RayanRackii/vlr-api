using System.Security.Claims;
using Platform.Api.Authentication;

namespace Platform.Api.Tests.Fakes;

public sealed class FakePlatformAdminChecker : IPlatformAdminChecker
{
    private readonly IReadOnlyList<string> _normalizedEmails;

    public FakePlatformAdminChecker(params string[] emails)
    {
        _normalizedEmails = emails
            .Where(email => !string.IsNullOrWhiteSpace(email))
            .Select(email => email.Trim().ToLowerInvariant())
            .ToList();
    }

    public bool IsPlatformAdmin(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var email = user.FindFirst("email")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.Identity?.Name;

        return IsPlatformAdminEmail(email);
    }

    public bool IsPlatformAdminEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        return _normalizedEmails.Contains(email.Trim().ToLowerInvariant());
    }

    public IReadOnlyList<string> GetNormalizedEmails() => _normalizedEmails;
}
