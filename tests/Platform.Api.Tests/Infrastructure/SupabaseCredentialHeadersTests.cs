using System.Net.Http.Headers;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Infrastructure;

public sealed class SupabaseCredentialHeadersTests
{
    private const string SecretKey = "sb_secret_dummy";
    private const string PublishableKey = "sb_publishable_dummy";
    private const string DummyJwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJkdW1teSJ9.dummy-signature-not-a-secret";

    [Fact]
    public void Apply_secret_key_sets_apikey_only()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        SupabaseCredentialHeaders.Apply(request.Headers, SecretKey);

        Assert.Equal(SecretKey, GetApiKey(request.Headers));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void Apply_jwt_sets_apikey_and_bearer()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        SupabaseCredentialHeaders.Apply(request.Headers, DummyJwt);

        Assert.Equal(DummyJwt, GetApiKey(request.Headers));
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(DummyJwt, request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void Apply_publishable_key_throws_without_leaking_credential()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SupabaseCredentialHeaders.Apply(request.Headers, PublishableKey));

        Assert.DoesNotContain(PublishableKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(PublishableKey, ex.ToString(), StringComparison.Ordinal);
        Assert.Contains("publishable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_unknown_format_throws_without_leaking_credential()
    {
        const string unknown = "not-a-valid-supabase-credential";
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        var ex = Assert.Throws<InvalidOperationException>(
            () => SupabaseCredentialHeaders.Apply(request.Headers, unknown));

        Assert.DoesNotContain(unknown, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(unknown, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_trims_credential_before_classifying()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/");

        SupabaseCredentialHeaders.Apply(request.Headers, $"  {SecretKey}  ");

        Assert.Equal(SecretKey, GetApiKey(request.Headers));
        Assert.Null(request.Headers.Authorization);
    }

    private static string? GetApiKey(HttpRequestHeaders headers)
    {
        return headers.TryGetValues("apikey", out var values)
            ? Assert.Single(values)
            : null;
    }
}
