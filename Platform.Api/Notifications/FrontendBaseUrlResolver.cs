namespace Platform.Api.Notifications;

/// <summary>
/// Resolves the public frontend origin used in invite / notification links.
/// Never emits localhost links when the host runs in Production.
/// </summary>
public static class FrontendBaseUrlResolver
{
    public const string ProductionDefault = "https://rolvix.com.br";
    public const string DevelopmentDefault = "http://localhost:5173";

    public static string Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["App:FrontendBaseUrl"]?.Trim().TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(configured) && !IsLocalUrl(configured))
        {
            return configured;
        }

        if (environment.IsProduction())
        {
            return ProductionDefault;
        }

        return string.IsNullOrWhiteSpace(configured)
            ? DevelopmentDefault
            : configured;
    }

    private static bool IsLocalUrl(string url) =>
        url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
}
