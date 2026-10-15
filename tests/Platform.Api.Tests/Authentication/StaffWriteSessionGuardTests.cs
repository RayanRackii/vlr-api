using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Authentication;

public sealed class StaffWriteSessionGuardTests
{
    private const string UserToken = "user-access-token";
    private const string ServiceRoleKey = "eyJhbGciOiJIUzI1NiJ9.service-role";
    private const string SecretBody = "revoked-session-payload-do-not-leak";

    [Fact]
    public async Task Post_gotrue_rejects_the_session_and_the_write_fails()
    {
        var handler = new StatusHandler(HttpStatusCode.Forbidden, SecretBody);
        var guard = CreateGuard(handler);
        var context = CreateContext(HttpMethods.Post);

        await guard.ValidateAsync(context);

        Assert.NotNull(context.Result);
        Assert.False(context.Result.Succeeded);
        Assert.DoesNotContain(UserToken, context.Result.Failure?.Message ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(SecretBody, context.Result.Failure?.Message ?? "", StringComparison.Ordinal);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith("/user", request.RequestUri?.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(UserToken, request.Headers.Authorization?.Parameter);
        Assert.Equal(ServiceRoleKey, request.Headers.GetValues("apikey").Single());
        Assert.NotEqual(ServiceRoleKey, request.Headers.Authorization?.Parameter);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Client_error_from_gotrue_rejects_the_write(HttpStatusCode status)
    {
        var guard = CreateGuard(new StatusHandler(status, SecretBody));
        var context = CreateContext(HttpMethods.Put);

        await guard.ValidateAsync(context);

        Assert.False(context.Result?.Succeeded);
    }

    [Fact]
    public async Task Active_session_allows_the_write()
    {
        var guard = CreateGuard(new StatusHandler(HttpStatusCode.OK, """{"id":"user"}"""));
        var context = CreateContext(HttpMethods.Patch);

        await guard.ValidateAsync(context);

        Assert.Null(context.Result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Auth_outage_fails_the_write_closed_without_looking_like_logout(HttpStatusCode status)
    {
        var guard = CreateGuard(new StatusHandler(status, SecretBody));
        var context = CreateContext(HttpMethods.Delete);

        var exception = await Assert.ThrowsAsync<StaffSessionStoreUnavailableException>(
            () => guard.ValidateAsync(context));

        Assert.Equal("Staff session check failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(UserToken, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretBody, exception.Message, StringComparison.Ordinal);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Transport_failure_fails_the_write_closed()
    {
        var guard = CreateGuard(new ThrowingHandler(new HttpRequestException("down")));
        var context = CreateContext(HttpMethods.Post);

        await Assert.ThrowsAsync<StaffSessionStoreUnavailableException>(
            () => guard.ValidateAsync(context));
    }

    [Fact]
    public async Task Probe_timeout_fails_the_write_closed()
    {
        var guard = CreateGuard(new ThrowingHandler(new TaskCanceledException("timeout")));
        var context = CreateContext(HttpMethods.Post);

        await Assert.ThrowsAsync<StaffSessionStoreUnavailableException>(
            () => guard.ValidateAsync(context));
    }

    [Fact]
    public async Task Request_abort_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var guard = CreateGuard(new ThrowingHandler(new TaskCanceledException("aborted")));
        var context = CreateContext(HttpMethods.Post);
        context.HttpContext.RequestAborted = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => guard.ValidateAsync(context));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Safe_methods_do_not_call_gotrue(string method)
    {
        var handler = new StatusHandler(HttpStatusCode.Forbidden, SecretBody);
        var guard = CreateGuard(handler);
        var context = CreateContext(method);

        await guard.ValidateAsync(context);

        Assert.Empty(handler.Requests);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Missing_bearer_on_a_write_is_rejected()
    {
        var handler = new StatusHandler(HttpStatusCode.OK, "{}");
        var guard = CreateGuard(handler);
        var context = CreateContext(HttpMethods.Post, bearer: null);

        await guard.ValidateAsync(context);

        Assert.False(context.Result?.Succeeded);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Supabase_jwt_events_invoke_the_write_guard()
    {
        var called = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSupabaseAuthentication(Config());
        services.AddSingleton<IStaffWriteSessionGuard>(new RecordingGuard(() => called++));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var options = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(SupabaseJwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(options.Events.OnTokenValidated);

        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Method = HttpMethods.Post;
        http.Request.Headers.Authorization = "Bearer " + UserToken;
        var scheme = new AuthenticationScheme(
            SupabaseJwtBearerDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(JwtBearerHandler));
        var context = new TokenValidatedContext(http, scheme, options)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity("test")),
        };

        await options.Events.OnTokenValidated(context);

        Assert.Equal(1, called);
    }

    private static StaffWriteSessionGuard CreateGuard(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://auth.example.test/auth/v1/"),
            Timeout = TimeSpan.FromSeconds(3),
        };
        var probe = new GoTrueAccessSessionProbe(
            http,
            Options.Create(new SupabaseOptions
            {
                Url = "https://auth.example.test",
                JwtSecret = "test-jwt-secret",
                ServiceRoleKey = ServiceRoleKey,
            }));
        return new StaffWriteSessionGuard(probe, NullLogger<StaffWriteSessionGuard>.Instance);
    }

    private static TokenValidatedContext CreateContext(string method, string? bearer = UserToken)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        if (bearer is not null)
        {
            http.Request.Headers.Authorization = "Bearer " + bearer;
        }

        var scheme = new AuthenticationScheme("test", displayName: null, handlerType: typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        return new TokenValidatedContext(http, scheme, options)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity("test")),
        };
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:Url"] = "https://example.supabase.co",
                ["Supabase:JwtSecret"] = "customer-jwt-test-secret-key-32bytes!",
                ["Supabase:ServiceRoleKey"] = ServiceRoleKey,
            })
            .Build();

    private sealed class RecordingGuard(Action onCall) : IStaffWriteSessionGuard
    {
        public Task ValidateAsync(TokenValidatedContext context)
        {
            onCall();
            return Task.CompletedTask;
        }
    }

    private sealed class StatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
