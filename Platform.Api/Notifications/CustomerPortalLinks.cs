namespace Platform.Api.Notifications;

/// <summary>
/// Customer-portal links built from the web origin the API already resolves
/// (<see cref="FrontendBaseUrlResolver"/>). Path mode matches the portal routes
/// on that origin: <c>/t/{subdomain}/agenda</c>.
/// </summary>
public static class CustomerPortalLinks
{
    public static string? ReservationAgenda(
        string? frontendBaseUrl,
        string? subdomain,
        Guid reservationId)
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

        var builder = new UriBuilder(baseUri)
        {
            Path = $"/t/{Uri.EscapeDataString(sub)}/agenda",
            Query = $"reservation={reservationId:D}",
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
