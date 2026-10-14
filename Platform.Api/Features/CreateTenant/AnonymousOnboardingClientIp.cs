namespace Platform.Api.Features.CreateTenant;

/// <summary>
/// Same read as <c>PasswordRecoveryService</c> and <c>CustomerAuthService</c>:
/// the first <c>X-Forwarded-For</c> hop, otherwise <c>RemoteIpAddress</c>.
/// Issue #109 owns any change to that trust decision.
/// </summary>
internal static class AnonymousOnboardingClientIp
{
    public static string? From(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var forwarded = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var firstHop = forwarded.Split(',', 2)[0].Trim();
            if (firstHop.Length > 0)
            {
                return firstHop;
            }
        }

        return httpContext.Connection.RemoteIpAddress?.ToString();
    }
}
