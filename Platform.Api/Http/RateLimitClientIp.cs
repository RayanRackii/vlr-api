using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Platform.Api.Http;

/// <summary>
/// Partition key for the public per-IP rate limits (#109).
/// </summary>
/// <remarks>
/// The only header read is <see cref="RateLimitOptions.ClientIpHeader"/>
/// (ADR 0006 and ADR 0008). The default is <c>X-Real-IP</c>. <c>Fly-Client-IP</c>
/// is the other allowed name. <c>X-Forwarded-For</c>, <c>CF-Connecting-IP</c>, and
/// any other header are never read. A configured name outside that pair is not
/// read either.
/// <para>
/// One trimmed IPv4 or IPv6 value is stored as the canonical address: an IPv6
/// scope id is dropped, and an IPv4-mapped IPv6 address is stored as IPv4.
/// A missing or malformed value uses <see cref="ConnectionInfo.RemoteIpAddress"/>,
/// also canonical, which is how a private-network call that never crossed the
/// edge is keyed. The raw header text is not the key. When the socket address
/// is missing too, the result is null and the gates share one <c>unknown</c> bucket.
/// </para>
/// </remarks>
internal static class RateLimitClientIp
{
    internal const string DefaultHeaderName = "X-Real-IP";

    internal const string FlyClientIpHeaderName = "Fly-Client-IP";

    internal static readonly string[] AllowedHeaderNames =
    [
        DefaultHeaderName,
        FlyClientIpHeaderName,
    ];

    public static string? From(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        if (TrySelectHeaderName(ConfiguredHeader(httpContext), out var headerName)
            && TryCanonical(httpContext, headerName, out var canonical))
        {
            return canonical;
        }

        return Canonical(httpContext.Connection.RemoteIpAddress);
    }

    internal static bool TrySelectHeaderName(string? configured, out string headerName)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            headerName = DefaultHeaderName;
            return true;
        }

        var trimmed = configured.Trim();
        foreach (var allowed in AllowedHeaderNames)
        {
            if (string.Equals(trimmed, allowed, StringComparison.OrdinalIgnoreCase))
            {
                headerName = allowed;
                return true;
            }
        }

        headerName = string.Empty;
        return false;
    }

    private static string? ConfiguredHeader(HttpContext httpContext)
    {
        var options = httpContext.RequestServices?.GetService<IOptions<RateLimitOptions>>();
        return options?.Value.ClientIpHeader;
    }

    private static bool TryCanonical(HttpContext httpContext, string headerName, out string canonical)
    {
        canonical = string.Empty;
        var values = httpContext.Request.Headers[headerName];
        if (values.Count != 1)
        {
            return false;
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var candidate = raw.Trim();
        if (candidate.Contains(',')
            || candidate.Contains(' ')
            || candidate.Contains('\t')
            || candidate.Contains('\r')
            || candidate.Contains('\n'))
        {
            return false;
        }

        if (!IPAddress.TryParse(candidate, out var address) || address is null)
        {
            return false;
        }

        var text = Canonical(address);
        if (text is null)
        {
            return false;
        }

        canonical = text;
        return true;
    }

    private static string? Canonical(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            address = new IPAddress(address.GetAddressBytes());
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }
}
