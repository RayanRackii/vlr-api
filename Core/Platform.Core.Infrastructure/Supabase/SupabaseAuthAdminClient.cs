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

    public async Task<string?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();

        // GoTrue Admin API: use `filter`, not `email` (email alone is ignored).
        var relativeUri =
            $"admin/users?page=1&per_page=50&filter={Uri.EscapeDataString(normalized)}";

        using var request = CreateAdminRequest(HttpMethod.Get, relativeUri);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new SupabaseAuthAdminException(
                $"Failed to look up Supabase user by email. Response: {responseBody}",
                (int)response.StatusCode);
        }

        using var document = JsonDocument.Parse(responseBody);

        if (!document.RootElement.TryGetProperty("users", out var usersElement)
            || usersElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var userElement in usersElement.EnumerateArray())
        {
            if (!EmailMatches(userElement, normalized))
            {
                continue;
            }

            var id = userElement.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }

    private static bool EmailMatches(JsonElement userElement, string normalizedEmail)
    {
        if (userElement.TryGetProperty("email", out var emailElement))
        {
            var candidate = emailElement.GetString();
            if (string.Equals(candidate, normalizedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!userElement.TryGetProperty("identities", out var identities)
            || identities.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var identity in identities.EnumerateArray())
        {
            if (!identity.TryGetProperty("identity_data", out var identityData)
                || identityData.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!identityData.TryGetProperty("email", out var identityEmail))
            {
                continue;
            }

            if (string.Equals(
                    identityEmail.GetString(),
                    normalizedEmail,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> UserExistsAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAdminRequest(HttpMethod.Get, $"admin/users/{supabaseUserId}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task UpdateUserAppMetadataAsync(
        string supabaseUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        await PutAppMetadataAsync(
            supabaseUserId,
            new { tenant_id = tenantId.ToString() },
            cancellationToken);
    }

    public async Task ClearUserTenantAppMetadataAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default)
    {
        await PutAppMetadataAsync(
            supabaseUserId,
            new { tenant_id = (string?)null },
            cancellationToken);
    }

    public async Task SetUserPasswordAsync(
        string supabaseUserId,
        string password,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAdminRequest(HttpMethod.Put, $"admin/users/{supabaseUserId}");
        request.Content = JsonContent.Create(
            new
            {
                password,
                email_confirm = true,
            },
            options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new SupabaseAuthAdminException(
                $"Failed to update Supabase user password. Response: {responseBody}",
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

    public async Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
        string email,
        string redirectTo,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var normalizedRedirect = redirectTo.Trim();

        using var request = CreateAdminRequest(HttpMethod.Post, "admin/generate_link");
        request.Content = JsonContent.Create(
            new
            {
                type = "recovery",
                email = normalizedEmail,
                redirect_to = normalizedRedirect,
            },
            options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new SupabaseAuthAdminException(
                $"Failed to generate recovery link. Response: {responseBody}",
                (int)response.StatusCode);
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var properties = root.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object
                ? props
                : root;

        var hashedToken = TryReadString(properties, "hashed_token")
            ?? TryReadString(root, "hashed_token");

        if (string.IsNullOrWhiteSpace(hashedToken))
        {
            throw new SupabaseAuthAdminException(
                "Supabase generate_link response did not include hashed_token.",
                502);
        }

        var actionLink = TryReadString(properties, "action_link")
            ?? TryReadString(root, "action_link");

        return new SupabaseRecoveryLink(hashedToken, actionLink);
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private async Task PutAppMetadataAsync(
        string supabaseUserId,
        object appMetadata,
        CancellationToken cancellationToken)
    {
        using var request = CreateAdminRequest(HttpMethod.Put, $"admin/users/{supabaseUserId}");
        request.Content = JsonContent.Create(
            new { app_metadata = appMetadata },
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

    private HttpRequestMessage CreateAdminRequest(HttpMethod method, string relativeUri)
    {
        var request = new HttpRequestMessage(method, relativeUri);
        request.Headers.Add("apikey", _options.ServiceRoleKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);
        return request;
    }
}
