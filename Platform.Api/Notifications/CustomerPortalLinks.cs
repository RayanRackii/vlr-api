namespace Platform.Api.Notifications;

/// <summary>
/// Customer-portal links. With a tenant host template (<c>App:TenantPortalHostTemplate</c>,
/// default <c>{subdomain}.rolvix.com.br</c>) the link targets the tenant's own host in
/// host mode (<c>https://{subdomain}.base/agenda</c>), which is the route the web serves on
/// tenant hosts. Without a template it falls back to path mode on the web origin
/// (<c>/t/{subdomain}/agenda</c>). Scheme always comes from the resolved web origin.
/// </summary>
public static class CustomerPortalLinks
{
    public static string? ReservationAgenda(
        string? frontendBaseUrl,
        string? subdomain,
        Guid reservationId,
        string? tenantPortalHostTemplate = null)
    {
        if (string.IsNullOrWhiteSpace(frontendBaseUrl) || string.IsNullOrWhiteSpace(subdomain))
        {
            return null;
        }

        var trimmed = frontendBaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var sub = subdomain.Trim().ToLowerInvariant();
        if (!IsPortalLabel(sub))
        {
            return null;
        }

        var query = $"reservation={reservationId:D}";
        if (!string.IsNullOrWhiteSpace(tenantPortalHostTemplate))
        {
            var template = tenantPortalHostTemplate.Trim();
            if (!template.Contains("{subdomain}", StringComparison.Ordinal))
            {
                return null;
            }

            var host = template.Replace("{subdomain}", sub, StringComparison.Ordinal).ToLowerInvariant();
            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                return null;
            }

            return new UriBuilder(baseUri.Scheme, host)
            {
                Path = "/agenda",
                Query = query,
            }.Uri.AbsoluteUri;
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = $"/t/{Uri.EscapeDataString(sub)}/agenda",
            Query = query,
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// Same label rule as admin subdomain writes: 2–63 chars, <c>[a-z0-9-]</c>,
    /// no leading or trailing hyphen. <c>.</c> and <c>..</c> are rejected so
    /// <see cref="Uri"/> cannot collapse <c>/t/../agenda</c>.
    /// </summary>
    private static bool IsPortalLabel(string subdomain)
    {
        if (subdomain is "." or "..")
        {
            return false;
        }

        if (subdomain.Length is < 2 or > 63)
        {
            return false;
        }

        if (subdomain[0] == '-' || subdomain[^1] == '-')
        {
            return false;
        }

        foreach (var character in subdomain)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
