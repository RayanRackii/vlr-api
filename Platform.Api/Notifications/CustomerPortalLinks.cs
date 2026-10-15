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
        var builder = new UriBuilder(baseUri)
        {
            Path = $"/t/{Uri.EscapeDataString(sub)}/agenda",
            Query = $"reservation={reservationId:D}",
            Fragment = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }
}
