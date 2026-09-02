using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Platform.Api.Authentication;

public interface IPlatformAdminChecker
{
    bool IsPlatformAdmin(ClaimsPrincipal? user);

    bool IsPlatformAdminEmail(string? email);

    /// <summary>Normalized (trim + lower) allowlist emails for query filters.</summary>
    IReadOnlyList<string> GetNormalizedEmails();
}

public sealed class PlatformAdminChecker(IOptions<PlatformAdminOptions> options) : IPlatformAdminChecker
{
    public bool IsPlatformAdmin(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (CustomerClaimTypes.IsCustomer(user))
        {
            return false;
        }

        return IsPlatformAdminEmail(ResolveEmail(user));
    }

    public bool IsPlatformAdminEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        return GetNormalizedEmailSet().Contains(email.Trim());
    }

    public IReadOnlyList<string> GetNormalizedEmails() =>
        GetNormalizedEmailSet().ToList();

    private HashSet<string> GetNormalizedEmailSet() =>
        options.Value.Emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? ResolveEmail(ClaimsPrincipal user)
    {
        return user.FindFirst("email")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.Identity?.Name;
    }
}

public sealed class PlatformAdminRequirement : IAuthorizationRequirement;

public sealed class PlatformAdminAuthorizationHandler(IPlatformAdminChecker checker)
    : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformAdminRequirement requirement)
    {
        if (checker.IsPlatformAdmin(context.User))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
