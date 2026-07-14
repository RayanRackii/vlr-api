using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Platform.Api.Authentication;

public interface IPlatformAdminChecker
{
    bool IsPlatformAdmin(ClaimsPrincipal? user);
}

public sealed class PlatformAdminChecker(IOptions<PlatformAdminOptions> options) : IPlatformAdminChecker
{
    public bool IsPlatformAdmin(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var email = ResolveEmail(user);

        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var allowed = options.Value.Emails
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allowed.Contains(email);
    }

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
