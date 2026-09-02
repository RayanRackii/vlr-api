using System.Net.Http.Headers;

namespace Platform.Core.Infrastructure.Supabase;

public static class SupabaseCredentialHeaders
{
    public static void Apply(HttpRequestHeaders headers, string credential)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var trimmed = (credential ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException(
                "Supabase:ServiceRoleKey is not a valid privileged backend credential.");
        }

        if (trimmed.StartsWith("sb_publishable_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A privileged backend credential cannot be a publishable key.");
        }

        headers.Remove("apikey");
        headers.Remove("Authorization");

        if (trimmed.StartsWith("sb_secret_", StringComparison.OrdinalIgnoreCase))
        {
            headers.TryAddWithoutValidation("apikey", trimmed);
            return;
        }

        if (trimmed.StartsWith("eyJ", StringComparison.Ordinal))
        {
            headers.TryAddWithoutValidation("apikey", trimmed);
            headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmed);
            return;
        }

        throw new InvalidOperationException(
            "Supabase:ServiceRoleKey is not a valid privileged backend credential.");
    }
}
