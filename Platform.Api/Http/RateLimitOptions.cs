using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Platform.Api.Http;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public string ClientIpHeader { get; set; } = RateLimitClientIp.DefaultHeaderName;
}

internal sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        if (RateLimitClientIp.TrySelectHeaderName(options.ClientIpHeader, out _))
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"RateLimit:ClientIpHeader must be one of: {string.Join(", ", RateLimitClientIp.AllowedHeaderNames)}.");
    }
}

public static class RateLimitServiceCollectionExtensions
{
    public static IServiceCollection AddRateLimitClientIp(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>()
            .Bind(configuration.GetSection(RateLimitOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();
        return services;
    }
}
