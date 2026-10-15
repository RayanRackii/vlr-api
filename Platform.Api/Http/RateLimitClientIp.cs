using System.Net;
using System.Net.Sockets;

namespace Platform.Api.Http;

/// <summary>
/// Partition key for the public per-IP rate limits (#109).
/// </summary>
/// <remarks>
/// Railway's edge overwrites <c>X-Real-IP</c> and <c>X-Forwarded-For</c>. The hop
/// count is not fixed, and Railway does not publish proxy ranges, so this process
/// does not copy forwarding headers onto <see cref="ConnectionInfo.RemoteIpAddress"/>.
/// <c>X-Forwarded-For</c> and <c>CF-Connecting-IP</c> are never read.
/// <para>
/// Public ingress uses <c>X-Real-IP</c> when that header has exactly one value and
/// the value is one IP address. The stored key is the canonical address: an IPv6
/// scope id is dropped, and an IPv4-mapped IPv6 address is stored as IPv4, so
/// alternate spellings of the same address share one bucket.
/// </para>
/// <para>
/// A missing or malformed header — the usual case for a private-network call that
/// never crossed the edge — falls back to <see cref="ConnectionInfo.RemoteIpAddress"/>.
/// The raw header text is not the key. Rotating garbage, a list, or an extra header
/// value stays on that socket address and cannot open a new bucket. When the socket
/// address is missing too, the result is null; the gates then share one
/// <c>unknown</c> bucket and still count the attempt.
/// </para>
/// <para>
/// A caller who can reach the process without the edge can still present any single
/// well-formed <c>X-Real-IP</c> and choose a bucket. That is the residual risk of
/// trusting the header the edge overwrites on public ingress.
/// </para>
/// </remarks>
internal static class RateLimitClientIp
{
    internal const string HeaderName = "X-Real-IP";

    public static string? From(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        if (TryCanonicalRealIp(httpContext, out var realIp))
        {
            return realIp;
        }

        return Canonical(httpContext.Connection.RemoteIpAddress);
    }

    private static bool TryCanonicalRealIp(HttpContext httpContext, out string canonical)
    {
        canonical = string.Empty;
        var values = httpContext.Request.Headers[HeaderName];
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
