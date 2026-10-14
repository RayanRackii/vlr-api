using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Core.Infrastructure;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Authentication;

public sealed class SupabaseAuthAdminTenantMetadataCacheTests
{
    private const string ServiceRoleKey = "eyJ-test-service-role-key";

    [Fact]
    public void Request_timeout_is_configured_between_two_and_five_seconds()
    {
        Assert.InRange(SupabaseAuthAdminClient.RequestTimeout.TotalSeconds, 2d, 5d);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:Url"] = "https://auth.example.test",
                ["Supabase:JwtSecret"] = "test-jwt-secret",
                ["Supabase:ServiceRoleKey"] = ServiceRoleKey,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSupabaseAdminClient(configuration);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<ISupabaseAuthAdminClient>();
        var http = typeof(SupabaseAuthAdminClient)
            .GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client) as HttpClient;

        Assert.NotNull(http);
        Assert.Equal(SupabaseAuthAdminClient.RequestTimeout, http.Timeout);
        Assert.Same(
            provider.GetRequiredService<SupabaseUserTenantMetadataCache>(),
            provider.GetRequiredService<SupabaseUserTenantMetadataCache>());
    }

    [Fact]
    public async Task Cache_hit_skips_the_second_http_call_for_the_same_auth_id()
    {
        var authId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var handler = new TenantMetadataHandler { TenantId = tenantId };
        var cache = new SupabaseUserTenantMetadataCache();
        var first = CreateClient(handler, cache);
        var second = CreateClient(handler, cache);

        var initial = await first.GetUserTenantAppMetadataAsync(authId.ToString("D"));
        var again = await second.GetUserTenantAppMetadataAsync(authId.ToString("N").ToUpperInvariant());

        Assert.Equal(tenantId, initial);
        Assert.Equal(tenantId, again);
        Assert.Equal(1, handler.Gets);

        handler.TenantId = Guid.NewGuid();
        var otherAuthId = Guid.NewGuid();
        var other = await first.GetUserTenantAppMetadataAsync(otherAuthId.ToString("D"));

        Assert.Equal(handler.TenantId, other);
        Assert.Equal(2, handler.Gets);
    }

    [Fact]
    public async Task Enter_and_exit_invalidate_the_cached_tenant()
    {
        var authId = Guid.NewGuid();
        var enteredTenant = Guid.NewGuid();
        var handler = new TenantMetadataHandler { TenantId = enteredTenant };
        var cache = new SupabaseUserTenantMetadataCache();
        var client = CreateClient(handler, cache);

        Assert.Equal(enteredTenant, await client.GetUserTenantAppMetadataAsync(authId.ToString("D")));
        Assert.Equal(1, handler.Gets);

        var nextTenant = Guid.NewGuid();
        handler.TenantId = nextTenant;
        await client.UpdateUserAppMetadataAsync(authId.ToString("D").ToUpperInvariant(), nextTenant);

        var upper = authId.ToString("D").ToUpperInvariant();
        var lower = authId.ToString("D").ToLowerInvariant();
        Assert.Equal(nextTenant, await client.GetUserTenantAppMetadataAsync(upper));
        Assert.Equal(2, handler.Gets);
        Assert.Equal(nextTenant, await client.GetUserTenantAppMetadataAsync(lower));
        Assert.Equal(2, handler.Gets);

        handler.TenantId = null;
        await client.ClearUserTenantAppMetadataAsync(lower);

        Assert.Null(await client.GetUserTenantAppMetadataAsync(upper));
        Assert.Equal(3, handler.Gets);
        Assert.Null(await client.GetUserTenantAppMetadataAsync(lower));
        Assert.Equal(3, handler.Gets);
    }

    [Fact]
    public async Task Failed_read_is_not_cached()
    {
        var authId = Guid.NewGuid().ToString("D");
        var tenantId = Guid.NewGuid();
        var handler = new TenantMetadataHandler
        {
            StatusCode = HttpStatusCode.InternalServerError,
            TenantId = tenantId,
        };
        var client = CreateClient(handler, new SupabaseUserTenantMetadataCache());

        var failed = await Assert.ThrowsAsync<SupabaseAuthAdminException>(
            () => client.GetUserTenantAppMetadataAsync(authId));
        Assert.Equal(500, failed.StatusCode);

        handler.StatusCode = HttpStatusCode.OK;
        var resolved = await client.GetUserTenantAppMetadataAsync(authId);

        Assert.Equal(tenantId, resolved);
        Assert.Equal(2, handler.Gets);
    }

    [Fact]
    public async Task Invalid_json_is_not_cached_and_omits_the_payload()
    {
        var authId = Guid.NewGuid().ToString("D");
        var tenantId = Guid.NewGuid();
        var handler = new TenantMetadataHandler
        {
            RawBody = "{\"email\":\"person@example.com\",\"app_metadata\":",
        };
        var client = CreateClient(handler, new SupabaseUserTenantMetadataCache());

        var invalid = await Assert.ThrowsAsync<SupabaseAuthAdminException>(
            () => client.GetUserTenantAppMetadataAsync(authId));

        Assert.Equal(502, invalid.StatusCode);
        Assert.Null(invalid.InnerException);
        Assert.DoesNotContain("person@example.com", invalid.ToString(), StringComparison.Ordinal);

        handler.RawBody = null;
        handler.TenantId = tenantId;
        var resolved = await client.GetUserTenantAppMetadataAsync(authId);

        Assert.Equal(tenantId, resolved);
        Assert.Equal(2, handler.Gets);
    }

    [Fact]
    public async Task Configured_http_timeout_fails_the_read_and_is_not_cached()
    {
        var authId = Guid.NewGuid().ToString("D");
        var handler = new TenantMetadataHandler
        {
            Delay = TimeSpan.FromSeconds(5),
            TenantId = Guid.NewGuid(),
        };
        var client = CreateClient(
            handler,
            new SupabaseUserTenantMetadataCache(),
            TimeSpan.FromMilliseconds(200));

        var started = DateTimeOffset.UtcNow;
        var timeout = await Assert.ThrowsAsync<SupabaseAuthAdminException>(
            () => client.GetUserTenantAppMetadataAsync(authId));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(504, timeout.StatusCode);
        Assert.Contains("Timed out", timeout.Message, StringComparison.Ordinal);
        Assert.IsType<TaskCanceledException>(timeout.InnerException);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"timeout waited {elapsed}");

        handler.Delay = TimeSpan.Zero;
        var resolved = await client.GetUserTenantAppMetadataAsync(authId);

        Assert.Equal(handler.TenantId, resolved);
        Assert.Equal(2, handler.Gets);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_an_auth_timeout()
    {
        var handler = new TenantMetadataHandler { Delay = TimeSpan.FromSeconds(5) };
        var client = CreateClient(handler, new SupabaseUserTenantMetadataCache());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetUserTenantAppMetadataAsync(Guid.NewGuid().ToString("D"), cancellation.Token));
    }

    private static SupabaseAuthAdminClient CreateClient(
        HttpMessageHandler handler,
        SupabaseUserTenantMetadataCache cache,
        TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://auth.example.test/auth/v1/"),
        };
        if (timeout is TimeSpan configured)
        {
            http.Timeout = configured;
        }

        return new SupabaseAuthAdminClient(
            http,
            Options.Create(new SupabaseOptions
            {
                Url = "https://auth.example.test",
                JwtSecret = "test-jwt-secret",
                ServiceRoleKey = ServiceRoleKey,
            }),
            cache);
    }

    private sealed class TenantMetadataHandler : HttpMessageHandler
    {
        public int Gets { get; private set; }

        public Guid? TenantId { get; set; }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public TimeSpan Delay { get; set; }

        public string? RawBody { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Gets++;
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (request.Method != HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
            }

            var body = RawBody
                ?? (TenantId is Guid tenantId
                    ? "{\"app_metadata\":{\"tenant_id\":\"" + tenantId.ToString("D") + "\"}}"
                    : "{\"app_metadata\":{}}");

            return new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
