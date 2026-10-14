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
        Assert.Equal(TimeSpan.FromSeconds(10), SupabaseUserTenantMetadataCache.Duration);

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
    public void Store_with_the_captured_epoch_is_not_served_after_invalidate()
    {
        var cache = new SupabaseUserTenantMetadataCache();
        var authId = Guid.NewGuid().ToString("D");
        var staleTenant = Guid.NewGuid();
        var epoch = cache.CaptureEpoch(authId);

        cache.Invalidate(authId);
        cache.Store(authId, staleTenant, epoch);

        Assert.False(cache.TryGet(authId, out var served));
        Assert.Null(served);

        var current = cache.CaptureEpoch(authId.ToUpperInvariant());
        var freshTenant = Guid.NewGuid();
        cache.Store(authId, freshTenant, current);

        Assert.True(cache.TryGet(authId, out served));
        Assert.Equal(freshTenant, served);
    }

    [Fact]
    public async Task Configured_http_timeout_fails_the_metadata_write()
    {
        var authId = Guid.NewGuid().ToString("D");
        var cachedTenant = Guid.NewGuid();
        var handler = new TenantMetadataHandler { TenantId = cachedTenant };
        var cache = new SupabaseUserTenantMetadataCache();
        var client = CreateClient(handler, cache, TimeSpan.FromMilliseconds(200));

        Assert.Equal(cachedTenant, await client.GetUserTenantAppMetadataAsync(authId));
        Assert.Equal(1, handler.Gets);

        handler.Delay = TimeSpan.FromSeconds(5);
        var started = DateTimeOffset.UtcNow;
        var timeout = await Assert.ThrowsAsync<SupabaseAuthAdminException>(
            () => client.UpdateUserAppMetadataAsync(authId, Guid.NewGuid()));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(504, timeout.StatusCode);
        Assert.Contains("Timed out updating", timeout.Message, StringComparison.Ordinal);
        Assert.IsType<TaskCanceledException>(timeout.InnerException);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"timeout waited {elapsed}");

        handler.Delay = TimeSpan.Zero;
        var nextTenant = Guid.NewGuid();
        handler.TenantId = nextTenant;
        var resolved = await client.GetUserTenantAppMetadataAsync(authId);

        Assert.Equal(nextTenant, resolved);
        Assert.Equal(2, handler.Gets);
    }

    [Fact]
    public async Task Caller_cancellation_on_metadata_write_is_not_an_auth_timeout()
    {
        var handler = new TenantMetadataHandler { Delay = TimeSpan.FromSeconds(5) };
        var client = CreateClient(handler, new SupabaseUserTenantMetadataCache());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.UpdateUserAppMetadataAsync(
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid(),
                cancellation.Token));
    }

    [Fact]
    public async Task Expired_epochs_are_evicted_and_do_not_resurrect_a_stale_store()
    {
        var cache = new SupabaseUserTenantMetadataCache();
        var authId = Guid.NewGuid().ToString("D");
        var staleTenant = Guid.NewGuid();
        var epoch = cache.CaptureEpoch(authId);
        cache.Store(authId, staleTenant, epoch);
        Assert.True(cache.TryGet(authId, out var served));
        Assert.Equal(staleTenant, served);
        Assert.Equal(1, cache.TrackedAuthIdCount);

        cache.Invalidate(authId);
        cache.EvictExpired(DateTimeOffset.UtcNow);
        cache.Store(authId, staleTenant, epoch);
        Assert.False(cache.TryGet(authId, out served));
        Assert.Null(served);

        var kept = Guid.NewGuid().ToString("D");
        var keptTenant = Guid.NewGuid();
        var keptEpoch = cache.CaptureEpoch(kept);
        cache.Store(kept, keptTenant, keptEpoch);

        cache.EvictExpired(DateTimeOffset.UtcNow.Add(SupabaseUserTenantMetadataCache.Duration).AddSeconds(1));

        Assert.Equal(0, cache.TrackedAuthIdCount);
        cache.Store(authId, staleTenant, epoch);
        Assert.False(cache.TryGet(authId, out served));
        Assert.False(cache.TryGet(kept, out served));

        var freshTenant = Guid.NewGuid();
        var freshEpoch = cache.CaptureEpoch(authId.ToUpperInvariant());
        cache.Store(authId, freshTenant, freshEpoch);
        Assert.True(cache.TryGet(authId, out served));
        Assert.Equal(freshTenant, served);
        Assert.Equal(1, cache.TrackedAuthIdCount);
    }

    [Fact]
    public void Epoch_map_refuses_new_auth_ids_past_the_cap()
    {
        var cache = new SupabaseUserTenantMetadataCache(maxTrackedAuthIds: 2);
        var first = Guid.NewGuid().ToString("D");
        var second = Guid.NewGuid().ToString("D");
        var overflow = Guid.NewGuid().ToString("D");
        var firstTenant = Guid.NewGuid();
        var overflowTenant = Guid.NewGuid();

        var firstEpoch = cache.CaptureEpoch(first);
        cache.Store(first, firstTenant, firstEpoch);
        cache.CaptureEpoch(second);
        var overflowEpoch = cache.CaptureEpoch(overflow);
        cache.Store(overflow, overflowTenant, overflowEpoch);

        Assert.Equal(2, cache.TrackedAuthIdCount);
        Assert.True(cache.TryGet(first, out var served));
        Assert.Equal(firstTenant, served);
        Assert.False(cache.TryGet(overflow, out served));
        Assert.Null(served);

        cache.Invalidate(first.ToUpperInvariant());
        cache.Store(first, overflowTenant, firstEpoch);
        Assert.False(cache.TryGet(first, out served));
        Assert.Equal(2, cache.TrackedAuthIdCount);
    }

    [Fact]
    public async Task Every_auth_admin_call_stops_on_the_shared_http_timeout()
    {
        var handler = new TenantMetadataHandler { Delay = TimeSpan.FromSeconds(5) };
        var client = CreateClient(
            handler,
            new SupabaseUserTenantMetadataCache(),
            TimeSpan.FromMilliseconds(200));
        var methods = typeof(SupabaseAuthAdminClient)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType))
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(9, methods.Length);

        foreach (var method in methods)
        {
            handler.ResetCounts();
            var args = method.GetParameters()
                .Select(parameter => SampleArgument(parameter.ParameterType))
                .ToArray();

            var started = DateTimeOffset.UtcNow;
            Task task;
            try
            {
                task = (Task)method.Invoke(client, args)!;
            }
            catch (TargetInvocationException ex)
            {
                task = Task.FromException(ex.InnerException ?? ex);
            }

            var exception = await Record.ExceptionAsync(() => task);
            var elapsed = DateTimeOffset.UtcNow - started;

            Assert.True(handler.Calls > 0, method.Name);
            Assert.NotNull(exception);
            var failure = exception!;
            Assert.True(
                failure is OperationCanceledException
                    || (failure is SupabaseAuthAdminException admin && admin.StatusCode == 504),
                $"{method.Name} threw {failure.GetType().Name}: {failure.Message}");
            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"{method.Name} waited {elapsed}");
        }
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

    private static object SampleArgument(Type type)
    {
        if (type == typeof(string))
        {
            return "person@example.test";
        }

        if (type == typeof(Guid))
        {
            return Guid.NewGuid();
        }

        if (type == typeof(CancellationToken))
        {
            return CancellationToken.None;
        }

        throw new InvalidOperationException($"No sample argument for {type.Name}.");
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

        public int Calls { get; private set; }

        public void ResetCounts()
        {
            Gets = 0;
            Calls = 0;
        }

        public Guid? TenantId { get; set; }

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public TimeSpan Delay { get; set; }

        public string? RawBody { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
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
