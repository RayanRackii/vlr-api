using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

    public const string ConcurrencyConflictCode = "concurrency.conflict";

    // EF optimistic concurrency, plus PostgreSQL serialization_failure (40001) and deadlock_detected (40P01).
    private static bool IsConcurrencyConflict(Exception? exception) =>
        exception is DbUpdateConcurrencyException
        || exception is PostgresException { SqlState: "40001" or "40P01" }
        || exception is DbUpdateException { InnerException: PostgresException { SqlState: "40001" or "40P01" } };

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

        if (exception is KeyNotFoundException)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "Resource not found.", code = "resource.not_found" });
            return;
        }

        if (exception is ArgumentException or FormatException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid request.", code = "request.invalid" });
            return;
        }

        if (IsConcurrencyConflict(exception))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "The resource was changed by another request. Retry.",
                code = ConcurrencyConflictCode,
            });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
    }
}
