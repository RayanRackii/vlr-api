using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;
using Platform.Api.Tests.Infrastructure;

namespace Platform.Api.Tests.Authentication;

public sealed class StaffWriteSessionPipelineTests
{
    private const string JwtSecret = "customer-jwt-test-secret-key-32bytes!";
    private const string ServiceRoleKey = "eyJhbGciOiJIUzI1NiJ9.service-role";
    private const string Path = "/api/test/staff-session";
    private const string SessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string SecretBody = "revoked-session-payload-do-not-leak";
    private const string Email = "staff@club.test";

    [Fact]
    public async Task Revoked_session_on_a_staff_write_returns_401()
    {
        var script = Script(
            HttpStatusCode.Forbidden,
            $$"""{"code":403,"error_code":"session_not_found","msg":"{{SecretBody}}","email":"{{Email}}"}""");
        using var host = await StartAsync(script);
        var token = Mint(SessionId);

        var response = await SendAsync(host, HttpMethod.Post, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoSecrets(await response.Content.ReadAsStringAsync(), response.Headers.WwwAuthenticate.ToString(), token);
        Assert.Equal(1, script.Calls);
        Assert.Equal(token, script.SeenBearer);
        Assert.Equal(ServiceRoleKey, script.SeenApiKey);
        Assert.NotEqual(ServiceRoleKey, script.SeenBearer);
    }

    [Fact]
    public async Task GoTrue_outage_on_a_staff_write_returns_generic_500()
    {
        var script = Script(
            HttpStatusCode.InternalServerError,
            $$"""{"msg":"{{SecretBody}}","email":"{{Email}}","session_id":"{{SessionId}}"}""");
        using var host = await StartAsync(script);
        var token = Mint(SessionId);

        var response = await SendAsync(host, HttpMethod.Post, token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("An unexpected error occurred.", document.RootElement.GetProperty("error").GetString());
        AssertNoSecrets(body, response.Headers.WwwAuthenticate.ToString(), token);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"message":"Invalid API key"}""")]
    [InlineData(HttpStatusCode.NotFound, """{"message":"no Route matched with those values"}""")]
    [InlineData(HttpStatusCode.Redirect, "")]
    [InlineData(HttpStatusCode.TemporaryRedirect, """{"error_code":"session_not_found"}""")]
    public async Task Gateway_and_redirect_answers_return_500(HttpStatusCode status, string body)
    {
        var script = Script(status, body);
        using var host = await StartAsync(script);
        var token = Mint(SessionId);

        var response = await SendAsync(host, HttpMethod.Post, token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("An unexpected error occurred.", document.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, script.Calls);
    }

    [Fact]
    public async Task Staff_read_does_not_call_gotrue()
    {
        var script = Script(HttpStatusCode.Forbidden, """{"error_code":"session_not_found"}""");
        using var host = await StartAsync(script);

        var response = await SendAsync(host, HttpMethod.Get, Mint(SessionId));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, script.Calls);
    }

    [Fact]
    public async Task Burst_of_writes_for_one_session_calls_gotrue_once_until_the_ttl()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var script = Script(HttpStatusCode.OK, """{"id":"user"}""", release);
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-10-16T12:00:00Z"));
        using var host = await StartAsync(script, clock);
        var token = Mint(SessionId);
        var other = Mint(SessionId, jti: "other-access-token");

        var first = SendAsync(host, HttpMethod.Post, token);
        var second = SendAsync(host, HttpMethod.Post, other);
        await script.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(1, script.Calls);
        release.TrySetResult();

        var responses = await Task.WhenAll(first, second);
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        Assert.Equal(1, script.Calls);

        script.Status = HttpStatusCode.Forbidden;
        script.Body = """{"error_code":"session_not_found","msg":"revoked"}""";
        var cached = await SendAsync(host, HttpMethod.Post, Mint(SessionId, jti: "third"));
        Assert.Equal(HttpStatusCode.NoContent, cached.StatusCode);
        Assert.Equal(1, script.Calls);

        clock.Advance(GoTrueSessionProbeCoordinator.DefaultTtl + TimeSpan.FromMilliseconds(1));
        var afterTtl = await SendAsync(host, HttpMethod.Post, Mint(SessionId, jti: "fourth"));
        Assert.Equal(HttpStatusCode.Unauthorized, afterTtl.StatusCode);
        Assert.Equal(2, script.Calls);
    }

    private static async Task<HttpResponseMessage> SendAsync(IHost host, HttpMethod method, string token)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(new HttpRequestMessage(method, Path));
    }

    private static void AssertNoSecrets(string body, string header, string token)
    {
        Assert.DoesNotContain(token, body, StringComparison.Ordinal);
        Assert.DoesNotContain(token, header, StringComparison.Ordinal);
        Assert.DoesNotContain(ServiceRoleKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ServiceRoleKey, header, StringComparison.Ordinal);
        Assert.DoesNotContain(SessionId, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Email, body, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretBody, body, StringComparison.Ordinal);
        Assert.DoesNotContain("Staff session check failed.", body, StringComparison.Ordinal);
    }

    private static GoTrueScript Script(HttpStatusCode status, string body, TaskCompletionSource? release = null) =>
        new()
        {
            Status = status,
            Body = body,
            Release = release,
        };

    private static string Mint(string sessionId, string? jti = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "https://example.supabase.co/auth/v1",
            claims:
            [
                new Claim("session_id", sessionId),
                new Claim("role", "ADMIN"),
                new Claim(JwtRegisteredClaimNames.Jti, jti ?? "jti"),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<IHost> StartAsync(GoTrueScript script, TimeProvider? clock = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(clock ?? TimeProvider.System);
                    services.AddSupabaseAuthentication(Configuration());
                    services.PostConfigure<JwtBearerOptions>(
                        SupabaseJwtBearerDefaults.AuthenticationScheme,
                        options =>
                        {
                            options.MetadataAddress = null!;
                            options.Authority = null;
                            options.ConfigurationManager = null;
                            options.RequireHttpsMetadata = false;
                            options.TokenValidationParameters.IssuerSigningKey =
                                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
                            options.TokenValidationParameters.ValidateIssuerSigningKey = true;
                        });
                    services.AddHttpClient(GoTrueAccessSessionProbe.HttpClientName)
                        .AddHttpMessageHandler(() => new ScriptHandler(script));
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(StaffSessionPipelineController).Assembly));
                            manager.FeatureProviders.Add(
                                new StaffSessionControllerFeatureProvider(typeof(StaffSessionPipelineController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UsePlatformExceptionHandler();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

        return host;
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:Url"] = "https://example.supabase.co",
                ["Supabase:JwtSecret"] = JwtSecret,
                ["Supabase:ServiceRoleKey"] = ServiceRoleKey,
            })
            .Build();

    private sealed class GoTrueScript
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public HttpStatusCode Status { get; set; }

        public string Body { get; set; } = "";

        public TaskCompletionSource? Release { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? SeenBearer { get; private set; }

        public string? SeenApiKey { get; private set; }

        public async Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                SeenBearer = request.Headers.Authorization?.Parameter;
                SeenApiKey = request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null;
                Entered.TrySetResult();
            }

            if (Release is not null)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body),
            };
        }
    }

    private sealed class ScriptHandler(GoTrueScript script) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            script.RespondAsync(request, cancellationToken);
    }

    private sealed class StaffSessionControllerFeatureProvider(Type controller) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == controller;
    }
}

[ApiController]
[Authorize]
public sealed class StaffSessionPipelineController : ControllerBase
{
    [HttpGet("/api/test/staff-session")]
    public IActionResult Get() => NoContent();

    [HttpPost("/api/test/staff-session")]
    public IActionResult Post() => NoContent();
}
