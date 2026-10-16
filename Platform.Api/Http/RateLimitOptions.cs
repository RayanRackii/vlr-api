using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Platform.Api.Http;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public string ClientIpHeader { get; set; } = RateLimitClientIp.DefaultHeaderName;
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
