using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Storage;

public sealed class SupabaseStorageProvider : IStorageProvider
{
    private const int SanitizedBodyLimit = 500;

    private readonly HttpClient _httpClient;
    private readonly StorageOptions _storageOptions;
    private readonly string _supabaseUrl;
    private readonly string _credential;
    private readonly ILogger<SupabaseStorageProvider> _logger;

    public SupabaseStorageProvider(
        HttpClient httpClient,
        IOptions<SupabaseOptions> supabaseOptions,
        IOptions<StorageOptions> storageOptions,
        ILogger<SupabaseStorageProvider> logger)
    {
        var supabase = supabaseOptions.Value;
        if (string.IsNullOrWhiteSpace(supabase.Url))
        {
            throw new InvalidOperationException("Supabase:Url is not configured.");
        }

        if (string.IsNullOrWhiteSpace(supabase.ServiceRoleKey))
        {
            throw new InvalidOperationException("Supabase:ServiceRoleKey is not configured.");
        }

        _storageOptions = storageOptions.Value;
        _logger = logger;
        _supabaseUrl = supabase.Url.TrimEnd('/');
        _credential = supabase.ServiceRoleKey.Trim();

        httpClient.BaseAddress = new Uri($"{_supabaseUrl}/storage/v1/");
        SupabaseCredentialHeaders.Apply(httpClient.DefaultRequestHeaders, _credential);
        _httpClient = httpClient;
    }

    public async Task UploadAsync(
        string bucket,
        string key,
        Stream stream,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await _httpClient.PostAsync(
            $"object/{Uri.EscapeDataString(bucket)}/{key}",
            content,
            cancellationToken);
        await EnsureStorageSuccessAsync(response, bucket, key, cancellationToken);
    }

    public string GetPublicUrl(string bucket, string key)
    {
        if (!string.Equals(bucket, _storageOptions.PublicBucket, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Public URLs are only available for the public bucket.");
        }

        return $"{_supabaseUrl}/storage/v1/object/public/{Uri.EscapeDataString(bucket)}/{key}";
    }

    public async Task<string> CreateSignedUrlAsync(
        string bucket,
        string key,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var payload = new { expiresIn = (int)ttl.TotalSeconds };
        using var response = await _httpClient.PostAsJsonAsync(
            $"object/sign/{Uri.EscapeDataString(bucket)}/{key}",
            payload,
            cancellationToken);
        await EnsureStorageSuccessAsync(response, bucket, key, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var signed = document.RootElement.TryGetProperty("signedURL", out var signedUrl)
            ? signedUrl.GetString()
            : document.RootElement.TryGetProperty("signedUrl", out var camel)
                ? camel.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(signed))
        {
            throw new InvalidOperationException("Storage provider did not return a signed URL.");
        }

        if (signed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return signed;
        }

        return $"{_supabaseUrl}/storage/v1{signed}";
    }

    public async Task DeleteAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            $"object/{Uri.EscapeDataString(bucket)}/{key}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Failed to delete storage object {Bucket}/{Key}: {Status} {Body}",
                bucket,
                key,
                (int)response.StatusCode,
                Sanitize(body));
        }
    }

    private async Task EnsureStorageSuccessAsync(
        HttpResponseMessage response,
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        TryParseStorageError(body, out var statusCode, out var error, out var code, out var message);

        var kind = Classify(response.StatusCode, statusCode, error, code, message);
        var exceptionMessage = kind switch
        {
            StorageProviderErrorKind.Conflict => StorageProviderException.ConflictMessage,
            StorageProviderErrorKind.Client => StorageProviderException.ClientMessage,
            _ => StorageProviderException.UpstreamMessage,
        };

        _logger.LogError(
            "Storage request failed. Status: {Status} SupabaseError: {SupabaseError} Message: {SanitizedMessage} Bucket: {Bucket} Key: {Key}",
            (int)response.StatusCode,
            Sanitize(code ?? error ?? statusCode ?? "unknown"),
            Sanitize(message ?? body),
            bucket,
            key);

        throw new StorageProviderException(kind, exceptionMessage);
    }

    private static StorageProviderErrorKind Classify(
        HttpStatusCode httpStatus,
        string? statusCode,
        string? error,
        string? code,
        string? message)
    {
        var blob = string.Join(
            ' ',
            new[] { ((int)httpStatus).ToString(), statusCode, error, code, message }
                .Where(static s => !string.IsNullOrWhiteSpace(s)));

        if (IsUpstreamAuthFailure(httpStatus, blob))
        {
            return StorageProviderErrorKind.Upstream;
        }

        if (IsConflict(httpStatus, statusCode, blob))
        {
            return StorageProviderErrorKind.Conflict;
        }

        if (IsClientRejection(httpStatus, statusCode, blob))
        {
            return StorageProviderErrorKind.Client;
        }

        return StorageProviderErrorKind.Upstream;
    }

    private static bool IsUpstreamAuthFailure(HttpStatusCode httpStatus, string blob)
    {
        if (httpStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return true;
        }

        return Contains(blob, "invalid jwt")
            || Contains(blob, "unauthorized_invalid_jwt_format")
            || Contains(blob, "unauthorized")
            || Contains(blob, "forbidden")
            || Contains(blob, "missing jwt");
    }

    private static bool IsConflict(HttpStatusCode httpStatus, string? statusCode, string blob)
    {
        if (httpStatus == HttpStatusCode.Conflict || IsStatus(statusCode, 409))
        {
            return true;
        }

        return Contains(blob, "duplicate")
            || Contains(blob, "already exists")
            || Contains(blob, "asset already exists");
    }

    private static bool IsClientRejection(HttpStatusCode httpStatus, string? statusCode, string blob)
    {
        if (httpStatus == HttpStatusCode.RequestEntityTooLarge || IsStatus(statusCode, 413))
        {
            return true;
        }

        return Contains(blob, "invalid mime")
            || Contains(blob, "mime type")
            || Contains(blob, "payload too large")
            || Contains(blob, "entity too large")
            || Contains(blob, "file size")
            || Contains(blob, "filesize");
    }

    private static bool IsStatus(string? value, int expected)
    {
        return int.TryParse(value, out var parsed) && parsed == expected;
    }

    private static bool Contains(string blob, string fragment) =>
        blob.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseStorageError(
        string body,
        out string? statusCode,
        out string? error,
        out string? code,
        out string? message)
    {
        statusCode = null;
        error = null;
        code = null;
        message = null;

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            statusCode = ReadStringish(root, "statusCode");
            error = ReadStringish(root, "error");
            code = ReadStringish(root, "code");
            message = ReadStringish(root, "message");
            return statusCode is not null || error is not null || code is not null || message is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadStringish(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.Null => null,
            _ => property.ToString(),
        };
    }

    private string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = text;
        if (redacted.Contains(_credential, StringComparison.Ordinal))
        {
            redacted = redacted.Replace(_credential, "[redacted]", StringComparison.Ordinal);
        }

        var bearer = "Bearer " + _credential;
        if (redacted.Contains(bearer, StringComparison.OrdinalIgnoreCase))
        {
            redacted = redacted.Replace(bearer, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }

        if (redacted.Length > SanitizedBodyLimit)
        {
            redacted = redacted[..SanitizedBodyLimit];
        }

        return redacted;
    }
}
