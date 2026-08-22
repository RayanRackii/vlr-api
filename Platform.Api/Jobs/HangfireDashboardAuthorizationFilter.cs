using Hangfire.Dashboard;
using Platform.Api.Authentication;

namespace Platform.Api.Jobs;

/// <summary>
/// Hangfire Dashboard is limited to the PlatformAdmin email allowlist.
/// Anonymous, Customer, and ordinary B2B JWTs are denied.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var checker = httpContext.RequestServices.GetRequiredService<IPlatformAdminChecker>();
        return checker.IsPlatformAdmin(httpContext.User);
    }
}
