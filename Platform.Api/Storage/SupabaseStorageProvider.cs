using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Storage;

public sealed class SupabaseStorageProvider : IStorageProvider
{
    private readonly HttpClient _httpClient;
    private readonly StorageOptions _storageOptions;
    private readonly string _supabaseUrl;
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

        httpClient.BaseAddress = new Uri($"{_supabaseUrl}/storage/v1/");
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", supabase.ServiceRoleKey);
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
        response.EnsureSuccessStatusCode();
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
        response.EnsureSuccessStatusCode();

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
            _logger.LogWarning(
                "Failed to delete storage object {Bucket}/{Key}: {Status}",
                bucket,
                key,
                (int)response.StatusCode);
        }
    }
}
