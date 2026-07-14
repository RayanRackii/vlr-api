using Hangfire.Dashboard;

namespace Platform.Api.Jobs;

/// <summary>
/// Allows Hangfire Dashboard in Development; requires an authenticated user otherwise.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var environment = httpContext.RequestServices
            .GetRequiredService<IWebHostEnvironment>();

        if (environment.IsDevelopment())
        {
            return true;
        }

        return httpContext.User.Identity?.IsAuthenticated == true;
    }
}
