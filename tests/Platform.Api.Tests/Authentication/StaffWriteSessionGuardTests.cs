using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;
using Platform.Api.Tests.Infrastructure;
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
        var handler = new StatusHandler(HttpStatusCode.Forbidden, GoTrueJson(GoTrueAccessSessionProbe.SessionNotFound));
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
    [InlineData(HttpStatusCode.Forbidden, "error_code")]
    [InlineData(HttpStatusCode.Unauthorized, "error_code")]
    [InlineData(HttpStatusCode.NotFound, "code")]
    public async Task Documented_revoked_session_code_rejects_the_write(HttpStatusCode status, string field)
    {
        var guard = CreateGuard(new StatusHandler(
            status,
            GoTrueJson(GoTrueAccessSessionProbe.SessionNotFound, field)));
        var context = CreateContext(HttpMethods.Put);

        await guard.ValidateAsync(context);

        Assert.False(context.Result?.Succeeded);
        Assert.DoesNotContain(SecretBody, context.Result?.Failure?.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Session_expired_code_rejects_the_write()
    {
        var guard = CreateGuard(new StatusHandler(
            HttpStatusCode.Forbidden,
            GoTrueJson(GoTrueAccessSessionProbe.SessionExpired)));
        var context = CreateContext(HttpMethods.Patch);

        await guard.ValidateAsync(context);

        Assert.False(context.Result?.Succeeded);
    }

    [Fact]
    public async Task Error_code_header_rejects_without_reading_a_matching_body()
    {
        var guard = CreateGuard(new StatusHandler(
            HttpStatusCode.Unauthorized,
            $$"""{"message":"{{SecretBody}}"}""",
            GoTrueAccessSessionProbe.SessionNotFound));
        var context = CreateContext(HttpMethods.Delete);

        await guard.ValidateAsync(context);

        Assert.False(context.Result?.Succeeded);
        Assert.DoesNotContain(SecretBody, context.Result?.Failure?.Message ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task Non_revoked_non_success_fails_closed_as_unavailable(HttpStatusCode status)
    {
        var guard = CreateGuard(new StatusHandler(status, $$"""{"message":"Invalid API key","detail":"{{SecretBody}}"}"""));
        var context = CreateContext(HttpMethods.Post);

        var exception = await Assert.ThrowsAsync<StaffSessionStoreUnavailableException>(
            () => guard.ValidateAsync(context));

        Assert.Equal("Staff session check failed.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Null(context.Result);
        Assert.DoesNotContain(SecretBody, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(UserToken, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Outage_status_stays_unavailable_even_when_the_body_names_a_revoked_session(HttpStatusCode status)
    {
        var guard = CreateGuard(new StatusHandler(
            status,
            GoTrueJson(GoTrueAccessSessionProbe.SessionNotFound),
            GoTrueAccessSessionProbe.SessionNotFound));
        var context = CreateContext(HttpMethods.Post);

        await Assert.ThrowsAsync<StaffSessionStoreUnavailableException>(
            () => guard.ValidateAsync(context));
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Misconfiguration_log_keeps_tokens_emails_and_session_ids_out()
    {
        const string sessionId = "11111111-2222-3333-4444-555555555555";
        const string email = "staff@club.test";
        var body = $$"""{"message":"Invalid API key","email":"{{email}}","session_id":"{{sessionId}}","access_token":"{{UserToken}}","apikey":"{{ServiceRoleKey}}","msg":"{{SecretBody}}"}""";
        var logs = new CollectingLogger();
        var guard = CreateGuard(new StatusHandler(HttpStatusCode.Unauthorized, body), logger: logs);
        var context = CreateContext(HttpMethods.Post, bearer: MintAccessToken(sessionId));

        await Assert.ThrowsAsync<StaffSessionStoreUnavailableException>(
            () => guard.ValidateAsync(context));

        var written = string.Join('\n', logs.Messages);
        Assert.Contains("HttpStatus=401", written, StringComparison.Ordinal);
        Assert.Contains("ErrorCode=absent", written, StringComparison.Ordinal);
        Assert.DoesNotContain("session_id=", written, StringComparison.Ordinal);
        Assert.DoesNotContain(UserToken, written, StringComparison.Ordinal);
        Assert.DoesNotContain(ServiceRoleKey, written, StringComparison.Ordinal);
        Assert.DoesNotContain(email, written, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionId, written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretBody, written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_session_is_probed_once_until_the_ttl_expires()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-10-16T12:00:00Z"));
        var coordinator = new GoTrueSessionProbeCoordinator(
            clock,
            GoTrueSessionProbeCoordinator.DefaultTtl,
            cacheCapacity: 8,
            inFlightCapacity: 8);
        var handler = new StatusHandler(HttpStatusCode.OK, "{}");
        var sessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var first = MintAccessToken(sessionId, jti: "one");
        var second = MintAccessToken(sessionId.ToUpperInvariant(), jti: "two");
        var probe = CreateProbe(handler, coordinator);

        Assert.Equal(GoTrueAccessSessionStatus.Active, await probe.ProbeAsync(first, CancellationToken.None));
        Assert.Equal(GoTrueAccessSessionStatus.Active, await probe.ProbeAsync(second, CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal(first, handler.Requests[0].Headers.Authorization?.Parameter);

        clock.Advance(GoTrueSessionProbeCoordinator.DefaultTtl + TimeSpan.FromMilliseconds(1));
        handler.Status = HttpStatusCode.Forbidden;
        handler.Body = GoTrueJson(GoTrueAccessSessionProbe.SessionNotFound);

        Assert.Equal(
            GoTrueAccessSessionStatus.Rejected,
            await probe.ProbeAsync(MintAccessToken(sessionId, jti: "three"), CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Concurrent_probes_for_one_session_share_a_single_gotrue_call()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler(HttpStatusCode.OK, "{}", release);
        var probe = CreateProbe(handler, new GoTrueSessionProbeCoordinator(TimeProvider.System));
        var sessionId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        var first = probe.ProbeAsync(MintAccessToken(sessionId, jti: "a"), CancellationToken.None);
        var second = probe.ProbeAsync(MintAccessToken(sessionId, jti: "b"), CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(1, handler.Calls);
        release.TrySetResult();

        Assert.Equal(GoTrueAccessSessionStatus.Active, await first);
        Assert.Equal(GoTrueAccessSessionStatus.Active, await second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cancelling_one_waiter_does_not_drop_the_shared_probe()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler(HttpStatusCode.OK, "{}", release);
        var probe = CreateProbe(handler, new GoTrueSessionProbeCoordinator(TimeProvider.System));
        var sessionId = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        using var cancellation = new CancellationTokenSource();

        var cancelled = probe.ProbeAsync(MintAccessToken(sessionId, jti: "a"), cancellation.Token);
        var other = probe.ProbeAsync(MintAccessToken(sessionId, jti: "b"), CancellationToken.None);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.TrySetResult();
        Assert.Equal(GoTrueAccessSessionStatus.Active, await other);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Cache_evicts_the_oldest_session_when_full()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-10-16T12:00:00Z"));
        var coordinator = new GoTrueSessionProbeCoordinator(
            clock,
            ttl: TimeSpan.FromMinutes(1),
            cacheCapacity: 1,
            inFlightCapacity: 4);
        var handler = new StatusHandler(HttpStatusCode.OK, "{}");
        var probe = CreateProbe(handler, coordinator);
        var older = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        var newer = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";

        await probe.ProbeAsync(MintAccessToken(older), CancellationToken.None);
        await probe.ProbeAsync(MintAccessToken(newer), CancellationToken.None);
        await probe.ProbeAsync(MintAccessToken(older, jti: "again"), CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Tokens_without_a_session_id_are_not_cached_or_coalesced_by_the_raw_token()
    {
        var handler = new StatusHandler(HttpStatusCode.OK, "{}");
        var probe = CreateProbe(handler, new GoTrueSessionProbeCoordinator(TimeProvider.System));

        await probe.ProbeAsync(UserToken, CancellationToken.None);
        await probe.ProbeAsync(UserToken, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_session_id_in_the_error_code_header_is_not_logged()
    {
        const string sessionId = "11111111-2222-3333-4444-555555555555";
        var logs = new CollectingLogger();
        var guard = CreateGuard(
            new StatusHandler(HttpStatusCode.Unauthorized, "{}", sessionId),
            logger: logs);
        var context = CreateContext(HttpMethods.Post, bearer: MintAccessToken(sessionId));

        await Assert.ThrowsAsync<StaffSessionStoreUnavailableException>(
            () => guard.ValidateAsync(context));

        var written = string.Join('\n', logs.Messages);
        Assert.Contains("ErrorCode=unrecognized", written, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionId, written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task In_flight_cap_lets_the_extra_probe_run_instead_of_queueing()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedHandler(HttpStatusCode.OK, "{}", release);
        var probe = CreateProbe(
            handler,
            new GoTrueSessionProbeCoordinator(
                TimeProvider.System,
                ttl: TimeSpan.FromMinutes(1),
                cacheCapacity: 8,
                inFlightCapacity: 0));
        var sessionId = "ffffffff-ffff-ffff-ffff-ffffffffffff";

        var first = probe.ProbeAsync(MintAccessToken(sessionId, jti: "a"), CancellationToken.None);
        var second = probe.ProbeAsync(MintAccessToken(sessionId, jti: "b"), CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (handler.Calls < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(2, handler.Calls);
        release.TrySetResult();
        Assert.Equal(GoTrueAccessSessionStatus.Active, await first);
        Assert.Equal(GoTrueAccessSessionStatus.Active, await second);
    }

    [Fact]
    public async Task User_banned_and_bad_jwt_are_not_treated_as_a_revoked_session()
    {
        foreach (var code in new[] { "user_banned", "user_not_found", "bad_jwt" })
        {
            var handler = new StatusHandler(HttpStatusCode.Forbidden, GoTrueJson(code));
            var probe = CreateProbe(handler, new GoTrueSessionProbeCoordinator(TimeProvider.System, TimeSpan.Zero, 0, 0));
            Assert.Equal(
                GoTrueAccessSessionStatus.Unavailable,
                await probe.ProbeAsync(UserToken, CancellationToken.None));
        }
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

    private static StaffWriteSessionGuard CreateGuard(
        HttpMessageHandler handler,
        GoTrueSessionProbeCoordinator? coordinator = null,
        ILogger<GoTrueAccessSessionProbe>? logger = null) =>
        new(CreateProbe(handler, coordinator, logger), NullLogger<StaffWriteSessionGuard>.Instance);

    private static GoTrueAccessSessionProbe CreateProbe(
        HttpMessageHandler handler,
        GoTrueSessionProbeCoordinator? coordinator = null,
        ILogger<GoTrueAccessSessionProbe>? logger = null)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://auth.example.test/auth/v1/"),
            Timeout = TimeSpan.FromSeconds(3),
        };
        return new GoTrueAccessSessionProbe(
            http,
            Options.Create(new SupabaseOptions
            {
                Url = "https://auth.example.test",
                JwtSecret = "test-jwt-secret",
                ServiceRoleKey = ServiceRoleKey,
            }),
            logger,
            coordinator);
    }

    private static string GoTrueJson(string errorCode, string field = "error_code") =>
        field == "code"
            ? $$"""{"code":"{{errorCode}}","message":"{{SecretBody}}"}"""
            : $$"""{"code":403,"error_code":"{{errorCode}}","msg":"{{SecretBody}}"}""";

    private static string MintAccessToken(string sessionId, string? jti = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-jwt-secret-key-32bytes-minimum!!")),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "https://auth.example.test/auth/v1",
            claims:
            [
                new Claim("session_id", sessionId),
                new Claim(JwtRegisteredClaimNames.Jti, jti ?? "jti"),
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
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

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly string? _errorCodeHeader;

        public StatusHandler(HttpStatusCode status, string body, string? errorCodeHeader = null)
        {
            Status = status;
            Body = body;
            _errorCodeHeader = errorCodeHeader;
        }

        public HttpStatusCode Status { get; set; }

        public string Body { get; set; }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body),
            };
            if (_errorCodeHeader is not null)
            {
                response.Headers.TryAddWithoutValidation(GoTrueAccessSessionProbe.ErrorCodeHeader, _errorCodeHeader);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class GatedHandler(
        HttpStatusCode status,
        string body,
        TaskCompletionSource release) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };
        }
    }

    private sealed class CollectingLogger : ILogger<GoTrueAccessSessionProbe>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
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
