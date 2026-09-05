namespace Platform.Core.Domain.Constants;

/// <summary>
/// First-level labels under <c>*.rolvix.com.br</c> that tenants cannot claim on create or rename.
/// Existing rows that already use a reserved slug are grandfathered.
/// </summary>
public static class ReservedTenantSubdomains
{
    public const string ReservedMessage = "This subdomain is reserved and cannot be used.";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        "www",
        "api",
        "app",
        "admin",
        "dev",
        "staging",
        "preview",
        "mail",
        "support",
    };

    public static bool IsReserved(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return All.Contains(value.Trim());
    }
}
