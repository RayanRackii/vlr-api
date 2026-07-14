using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Platform.Core.Infrastructure.Supabase;

public sealed class SupabaseAuthAdminClient : ISupabaseAuthAdminClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly SupabaseOptions _options;

    public SupabaseAuthAdminClient(HttpClient httpClient, IOptions<SupabaseOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<string> CreateUserAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAdminRequest(HttpMethod.Post, "admin/users");
        request.Content = JsonContent.Create(
            new
            {
                email,
                password,
                email_confirm = true,
            },
            options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new SupabaseAuthAdminException(
                $"Failed to create Supabase user. Response: {responseBody}",
                (int)response.StatusCode);
        }

        using var document = JsonDocument.Parse(responseBody);
        var userId = document.RootElement.GetProperty("id").GetString();

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new SupabaseAuthAdminException(
                "Supabase user creation response did not include a user id.",
                502);
        }

        return userId;
    }

    public async Task UpdateUserAppMetadataAsync(
        string supabaseUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAdminRequest(HttpMethod.Put, $"admin/users/{supabaseUserId}");
        request.Content = JsonContent.Create(
            new
            {
                app_metadata = new
                {
                    tenant_id = tenantId.ToString(),
                },
            },
            options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new SupabaseAuthAdminException(
                $"Failed to update Supabase user app_metadata. Response: {responseBody}",
                (int)response.StatusCode);
        }
    }

    public async Task DeleteUserAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAdminRequest(HttpMethod.Delete, $"admin/users/{supabaseUserId}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SupabaseAuthAdminException(
                $"Failed to delete Supabase user during compensation. Response: {responseBody}",
                (int)response.StatusCode);
        }
    }

    private HttpRequestMessage CreateAdminRequest(HttpMethod method, string relativeUri)
    {
        var request = new HttpRequestMessage(method, relativeUri);
        request.Headers.Add("apikey", _options.ServiceRoleKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);
        return request;
    }
}
