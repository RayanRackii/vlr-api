using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Platform.Api.Authentication;

/// <summary>
/// The staff session could not be checked with GoTrue. This is an infrastructure
/// failure, not a revoked session. The pipeline exception handler answers HTTP 500
/// with its generic body. This message stays off that body. The inner exception is
/// omitted so logs and the response cannot carry access tokens or Auth payloads.
/// </summary>
internal sealed class StaffSessionStoreUnavailableException : Exception
{
    public StaffSessionStoreUnavailableException()
        : base("Staff session check failed.")
    {
    }
}

internal interface IStaffWriteSessionGuard
{
    Task ValidateAsync(TokenValidatedContext context);
}

/// <summary>
/// On staff writes, confirms the Supabase access token's session is still accepted.
/// Reads (GET, HEAD, OPTIONS, TRACE) stay on local JWT validation: signature and
/// lifetime. A token GoTrue rejects fails the request (HTTP 401). A transport or
/// Auth outage fails the write closed as HTTP 500, not as a logout.
/// </summary>
internal sealed class StaffWriteSessionGuard(
    IGoTrueAccessSessionProbe probe,
    ILogger<StaffWriteSessionGuard> logger) : IStaffWriteSessionGuard
{
    public async Task ValidateAsync(TokenValidatedContext context)
    {
        if (IsSafeMethod(context.Request.Method))
        {
            return;
        }

        var accessToken = ReadBearer(context.Request);
        if (accessToken is null)
        {
            context.Fail("Session revoked.");
            return;
        }

        GoTrueAccessSessionStatus status;
        try
        {
            status = await probe.ProbeAsync(accessToken, context.HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogStoreFailure(logger, exception);
            throw new StaffSessionStoreUnavailableException();
        }

        if (status == GoTrueAccessSessionStatus.Active)
        {
            return;
        }

        if (status == GoTrueAccessSessionStatus.Rejected)
        {
            context.Fail("Session revoked.");
            return;
        }

        // Unavailable is already logged by the probe (status and safe error code only).
        throw new StaffSessionStoreUnavailableException();
    }

    internal static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method)
        || HttpMethods.IsTrace(method);

    private static string? ReadBearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static void LogStoreFailure(ILogger logger, Exception? exception)
    {
        logger.LogError(
            "Staff session check failed. ExceptionType={ExceptionType}",
            exception?.GetType().FullName ?? "Unavailable");
    }
}
