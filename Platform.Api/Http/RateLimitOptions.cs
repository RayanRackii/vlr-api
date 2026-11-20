using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Platform.Api.Http;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public string ClientIpHeader { get; set; } = RateLimitClientIp.DefaultHeaderName;

    /// <summary>Limits for the anonymous customer-auth endpoints that send SMS or e-mail.</summary>
    public CustomerAuthSendRateLimitOptions CustomerAuthSend { get; set; } = new();
}

public sealed class CustomerAuthSendRateLimitOptions
{
    /// <summary>Requests allowed per client IP inside one window.</summary>
    public int PermitLimitPerIp { get; set; } = 10;

    /// <summary>Requests allowed across all clients inside one window.</summary>
    public int GlobalPermitLimit { get; set; } = 300;

    public int WindowMinutes { get; set; } = 10;

    internal TimeSpan Window => TimeSpan.FromMinutes(Math.Max(1, WindowMinutes));
}

internal sealed record RateLimitPlatformRule(string Platform, string[] Variables, string ExpectedHeader);

internal static class RateLimitPlatformRules
{
    internal static readonly RateLimitPlatformRule[] All =
    [
        new("Fly", ["FLY_APP_NAME"], RateLimitClientIp.FlyClientIpHeaderName),
        new(
            "Railway",
            ["RAILWAY_ENVIRONMENT", "RAILWAY_PROJECT_ID"],
            RateLimitClientIp.DefaultHeaderName),
    ];
}

internal sealed class RateLimitOptionsValidator(IConfiguration configuration) : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        if (!RateLimitClientIp.TrySelectHeaderName(options.ClientIpHeader, out var selected))
        {
            return ValidateOptionsResult.Fail(
                $"RateLimit:ClientIpHeader must be one of: {string.Join(", ", RateLimitClientIp.AllowedHeaderNames)}.");
        }

        List<string>? failures = null;
        var send = options.CustomerAuthSend;
        if (send is null || send.PermitLimitPerIp < 1 || send.GlobalPermitLimit < 1 || send.WindowMinutes < 1)
        {
            failures = [];
            failures.Add(
                "RateLimit:CustomerAuthSend:PermitLimitPerIp, GlobalPermitLimit and WindowMinutes must each be at least 1.");
        }

        foreach (var rule in RateLimitPlatformRules.All)
        {
            var present = new List<string>(rule.Variables.Length);
            foreach (var variable in rule.Variables)
            {
                if (!string.IsNullOrWhiteSpace(configuration[variable]))
                {
                    present.Add(variable);
                }
            }

            if (present.Count == 0
                || string.Equals(selected, rule.ExpectedHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            failures ??= [];
            failures.Add(
                $"RateLimit:ClientIpHeader selects {selected}, but {rule.Platform} is detected ({string.Join(", ", present)}). Set RateLimit:ClientIpHeader to {rule.ExpectedHeader}.");
        }

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public static class RateLimitServiceCollectionExtensions
{
    public static IServiceCollection AddRateLimitClientIp(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(configuration);
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();
        return services;
    }
}
