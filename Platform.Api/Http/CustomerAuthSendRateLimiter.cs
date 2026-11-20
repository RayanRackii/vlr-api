using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Platform.Api.Http;

/// <summary>
/// Marks an anonymous customer-auth action that sends an SMS or e-mail. Marked endpoints are
/// throttled per client IP and globally by <see cref="CustomerAuthSendRateLimiting"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class CustomerAuthSendThrottleAttribute : Attribute
{
}

/// <summary>
/// ASP.NET Core rate limiter for the anonymous customer-auth send endpoints (review P1-A).
/// Per-IP partition key comes from <see cref="RateLimitClientIp"/> (ADR 0008). The per-IP limiter
/// runs first, so one abusive IP cannot burn the global budget with rejected requests.
/// </summary>
public static class CustomerAuthSendRateLimiting
{
    public const string RejectionMessage =
        "Muitas solicitações de código. Aguarde alguns minutos e tente novamente.";

    private const string UnmarkedPartition = "unmarked";
    private const string GlobalPartition = "customer-auth-send:global";
    private const string UnknownIp = "unknown";

    public static IServiceCollection AddCustomerAuthSendRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(PerIpPartition),
                PartitionedRateLimiter.Create<HttpContext, string>(GlobalCapPartition));
            options.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : Math.Max(1, (int)Math.Ceiling(Settings(context.HttpContext).Window.TotalSeconds));
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await response.WriteAsJsonAsync(new { error = RejectionMessage }, cancellationToken);
            };
        });

        return services;
    }

    private static bool IsMarked(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<CustomerAuthSendThrottleAttribute>() is not null;

    private static CustomerAuthSendRateLimitOptions Settings(HttpContext context) =>
        context.RequestServices?.GetService<IOptions<RateLimitOptions>>()?.Value.CustomerAuthSend
        ?? new CustomerAuthSendRateLimitOptions();

    private static RateLimitPartition<string> PerIpPartition(HttpContext context)
    {
        if (!IsMarked(context))
        {
            return RateLimitPartition.GetNoLimiter(UnmarkedPartition);
        }

        var settings = Settings(context);
        var ip = RateLimitClientIp.From(context) ?? UnknownIp;
        return RateLimitPartition.GetFixedWindowLimiter(
            ip,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.PermitLimitPerIp,
                Window = settings.Window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }

    private static RateLimitPartition<string> GlobalCapPartition(HttpContext context)
    {
        if (!IsMarked(context))
        {
            return RateLimitPartition.GetNoLimiter(UnmarkedPartition);
        }

        var settings = Settings(context);
        return RateLimitPartition.GetFixedWindowLimiter(
            GlobalPartition,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.GlobalPermitLimit,
                Window = settings.Window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }
}
