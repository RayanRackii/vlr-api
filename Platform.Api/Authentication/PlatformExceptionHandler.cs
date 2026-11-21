using Microsoft.AspNetCore.Diagnostics;
using Platform.Api.Services.Billing;

namespace Platform.Api.Authentication;

public static class PlatformExceptionHandler
{
    public static void UsePlatformExceptionHandler(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(WriteAsync);
        });
    }

    private static async Task WriteAsync(HttpContext context)
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

        if (exception is TenantResolutionException tenantResolutionException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = tenantResolutionException.Message });
            return;
        }

        if (exception is UnauthorizedAccessException unauthorizedAccessException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = unauthorizedAccessException.Message });
            return;
        }

        if (exception is PlanLimitReachedException planLimit)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = planLimit.Message,
                code = PlanLimitReachedException.Code,
                limit = planLimit.Limit,
                current = planLimit.Current,
                max = planLimit.Max,
                planKey = planLimit.PlanKey,
            });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
    }
}
