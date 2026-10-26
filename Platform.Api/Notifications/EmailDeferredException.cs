using System.Net;

namespace Platform.Api.Notifications;

/// <summary>A provider quota prevents sending until the next UTC day.</summary>
public sealed class EmailDeferredException() : HttpRequestException(
    "Email deferred because the provider daily quota was exceeded.",
    inner: null,
    statusCode: HttpStatusCode.TooManyRequests);
