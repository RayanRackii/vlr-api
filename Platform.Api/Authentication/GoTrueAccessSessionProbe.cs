using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Authentication;

internal enum GoTrueAccessSessionStatus
{
    Active,
    Rejected,
    Unavailable,
}

/// <summary>
/// Asks GoTrue whether the caller's access token still maps to a live session.
/// <c>GET /auth/v1/user</c> runs <c>requireAuthentication</c>, which loads
/// <c>auth.sessions</c> by the JWT <c>session_id</c>. Logout deletes that row, so
/// GoTrue answers 403 <c>session_not_found</c>. Signature and <c>exp</c> alone do not.
/// </summary>
internal interface IGoTrueAccessSessionProbe
{
    Task<GoTrueAccessSessionStatus> ProbeAsync(
        string accessToken,
        CancellationToken cancellationToken);
}

internal sealed class GoTrueAccessSessionProbe : IGoTrueAccessSessionProbe
{
    public const string HttpClientName = "supabase-auth-user";

    private readonly HttpClient _httpClient;
    private readonly SupabaseOptions _options;

    public GoTrueAccessSessionProbe(
        IHttpClientFactory httpClientFactory,
        IOptions<SupabaseOptions> options)
        : this(httpClientFactory.CreateClient(HttpClientName), options)
    {
    }

    internal GoTrueAccessSessionProbe(HttpClient httpClient, IOptions<SupabaseOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<GoTrueAccessSessionStatus> ProbeAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        // GoTrue reads this bearer and looks up session_id. The service role is only
        // the gateway apikey; it must not replace the caller's Authorization.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
        request.Headers.TryAddWithoutValidation("apikey", RequireApiKey());

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return GoTrueAccessSessionStatus.Active;
            }

            if ((int)response.StatusCode >= 500
                || response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return GoTrueAccessSessionStatus.Unavailable;
            }

            return GoTrueAccessSessionStatus.Rejected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return GoTrueAccessSessionStatus.Unavailable;
        }
        catch (HttpRequestException)
        {
            return GoTrueAccessSessionStatus.Unavailable;
        }
    }

    private string RequireApiKey()
    {
        var key = (_options.ServiceRoleKey ?? string.Empty).Trim();
        if (key.Length == 0
            || key.StartsWith("sb_publishable_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Supabase:ServiceRoleKey is not a valid privileged backend credential.");
        }

        return key;
    }
}
