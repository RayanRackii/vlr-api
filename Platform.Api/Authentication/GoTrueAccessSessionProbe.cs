using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
/// <c>auth.sessions</c> by the JWT <c>session_id</c>. Logout deletes that row.
/// GoTrue then returns <c>session_not_found</c> (legacy <c>error_code</c>, API
/// version 2024-01-01 string <c>code</c>, or the <c>x-sb-error-code</c> header).
/// <c>session_expired</c> is the documented invalid-session code. Signature and
/// <c>exp</c> alone do not show logout.
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

    internal const string SessionNotFound = "session_not_found";
    internal const string SessionExpired = "session_expired";
    internal const string ErrorCodeHeader = "x-sb-error-code";

    // Logged only from this set. Anything else, including a session id, is "unrecognized".
    private static readonly HashSet<string> LoggableErrorCodes = new(StringComparer.Ordinal)
    {
        SessionNotFound,
        SessionExpired,
        "bad_jwt",
        "user_banned",
        "user_not_found",
        "no_authorization",
        "unexpected_audience",
        "unexpected_failure",
        "validation_failed",
        "over_request_rate_limit",
        "request_timeout",
    };

    private const int MaxErrorBodyChars = 4096;

    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _httpClient;
    private readonly SupabaseOptions _options;
    private readonly ILogger<GoTrueAccessSessionProbe> _logger;
    private readonly GoTrueSessionProbeCoordinator _coordinator;

    public GoTrueAccessSessionProbe(
        IHttpClientFactory httpClientFactory,
        IOptions<SupabaseOptions> options,
        ILogger<GoTrueAccessSessionProbe> logger,
        TimeProvider timeProvider)
        : this(
            httpClientFactory,
            httpClient: null,
            options,
            logger,
            new GoTrueSessionProbeCoordinator(timeProvider))
    {
    }

    internal GoTrueAccessSessionProbe(
        HttpClient httpClient,
        IOptions<SupabaseOptions> options,
        ILogger<GoTrueAccessSessionProbe>? logger = null,
        GoTrueSessionProbeCoordinator? coordinator = null)
        : this(
            httpClientFactory: null,
            httpClient,
            options,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GoTrueAccessSessionProbe>.Instance,
            coordinator ?? new GoTrueSessionProbeCoordinator(TimeProvider.System))
    {
    }

    private GoTrueAccessSessionProbe(
        IHttpClientFactory? httpClientFactory,
        HttpClient? httpClient,
        IOptions<SupabaseOptions> options,
        ILogger<GoTrueAccessSessionProbe> logger,
        GoTrueSessionProbeCoordinator coordinator)
    {
        if (httpClientFactory is null && httpClient is null)
        {
            throw new ArgumentNullException(nameof(httpClientFactory));
        }

        _httpClientFactory = httpClientFactory;
        _httpClient = httpClient;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public Task<GoTrueAccessSessionStatus> ProbeAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var token = accessToken.Trim();
        var sessionId = TryReadSessionId(token);
        if (sessionId is null)
        {
            return ProbeCoreAsync(token, cancellationToken);
        }

        return _coordinator.ExecuteAsync(
            sessionId,
            _ => ProbeCoreAsync(token, CancellationToken.None),
            cancellationToken);
    }

    private async Task<GoTrueAccessSessionStatus> ProbeCoreAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        // GoTrue reads this bearer and looks up session_id. The service role is only
        // the gateway apikey; it must not replace the caller's Authorization.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("apikey", RequireApiKey());

        var client = _httpClient ?? _httpClientFactory!.CreateClient(HttpClientName);
        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return await ClassifyAsync(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            LogTransportFailure(exception);
            return GoTrueAccessSessionStatus.Unavailable;
        }
        catch (HttpRequestException exception)
        {
            LogTransportFailure(exception);
            return GoTrueAccessSessionStatus.Unavailable;
        }
    }

    private async Task<GoTrueAccessSessionStatus> ClassifyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return GoTrueAccessSessionStatus.Active;
        }

        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400
            || status >= 500
            || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            LogUnavailable(response.StatusCode, errorCode: null);
            return GoTrueAccessSessionStatus.Unavailable;
        }

        var headerCode = NormalizeCode(ReadSingleHeader(response, ErrorCodeHeader));
        if (IsRevokedSession(headerCode))
        {
            return GoTrueAccessSessionStatus.Rejected;
        }

        var bodyCode = NormalizeCode(await TryReadBodyCodeAsync(response, cancellationToken));
        if (IsRevokedSession(bodyCode))
        {
            return GoTrueAccessSessionStatus.Rejected;
        }

        LogUnavailable(response.StatusCode, headerCode ?? bodyCode);
        return GoTrueAccessSessionStatus.Unavailable;
    }

    private static bool IsRevokedSession(string? errorCode) =>
        errorCode is SessionNotFound or SessionExpired;

    private void LogUnavailable(HttpStatusCode statusCode, string? errorCode)
    {
        _logger.LogError(
            "Staff session check failed. HttpStatus={HttpStatus} ErrorCode={ErrorCode}",
            (int)statusCode,
            ForLog(errorCode));
    }

    private void LogTransportFailure(Exception exception)
    {
        _logger.LogError(
            "Staff session check failed. ExceptionType={ExceptionType}",
            exception.GetType().FullName ?? exception.GetType().Name);
    }

    private static string? ReadSingleHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        var code = enumerator.Current;
        return enumerator.MoveNext() ? null : code;
    }

    private static async Task<string?> TryReadBodyCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return null;
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxErrorBodyChars];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return null;
            }

            using var document = JsonDocument.Parse(new string(buffer, 0, read));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (document.RootElement.TryGetProperty("error_code", out var errorCode)
                && errorCode.ValueKind == JsonValueKind.String)
            {
                return errorCode.GetString();
            }

            // API version 2024-01-01 sends the textual code in `code` and the
            // human text in `message`. A numeric `code` is only the HTTP status.
            if (document.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String)
            {
                return code.GetString();
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadSessionId(string accessToken)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(accessToken))
            {
                return null;
            }

            var sessionId = handler.ReadJwtToken(accessToken)
                .Claims
                .FirstOrDefault(claim => claim.Type == "session_id")
                ?.Value;
            if (!Guid.TryParse(sessionId, out var parsed) || parsed == Guid.Empty)
            {
                return null;
            }

            return parsed.ToString("D");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length is 0 or > 64 ? null : trimmed;
    }

    private static string ForLog(string? errorCode)
    {
        if (errorCode is null)
        {
            return "absent";
        }

        return LoggableErrorCodes.Contains(errorCode) ? errorCode : "unrecognized";
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
