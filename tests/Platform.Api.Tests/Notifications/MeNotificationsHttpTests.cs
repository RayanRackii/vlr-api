using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Notifications.Controllers;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;

namespace Platform.Api.Tests.Notifications;

public sealed class MeNotificationsHttpTests
{
    [Fact]
    public async Task Unauthenticated_requests_return_401()
    {
        using var host = StartHost(new StubService());
        var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/notifications/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/notifications")).StatusCode);
    }

    [Theory]
    [InlineData("{}")] // none
    [InlineData("{\"all\":true,\"eventType\":\"catalog.order.created\"}")] // more than one
    [InlineData("{\"ids\":[\"11111111-1111-1111-1111-111111111111\"],\"all\":true}")] // more than one
    public async Task MarkRead_rejects_zero_or_multiple_selectors_with_400(string body)
    {
        var stub = new StubService();
        using var host = StartHost(stub);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestUserAuthentication.Header, "user-1");

        var response = await client.PostAsync(
            "/api/me/notifications/read",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(stub.MarkReadCalled);
    }

    [Fact]
    public async Task MarkRead_accepts_exactly_one_selector()
    {
        var stub = new StubService { Updated = 3 };
        using var host = StartHost(stub);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestUserAuthentication.Header, "user-1");

        var response = await client.PostAsJsonAsync("/api/me/notifications/read", new { all = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<MarkNotificationsReadResponse>();
        Assert.Equal(3, dto!.Updated);
        Assert.True(stub.MarkReadCalled);
    }

    [Fact]
    public async Task List_rejects_limit_over_50()
    {
        using var host = StartHost(new StubService());
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestUserAuthentication.Header, "user-1");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/me/notifications?limit=51")).StatusCode);
    }

    private static IHost StartHost(IMeNotificationsService service) => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddSingleton(service);
            services.AddSingleton<Platform.Core.Infrastructure.Persistence.ITenantProvider>(
                new Platform.Api.Tests.Fakes.FakeTenantProvider { TenantId = Guid.NewGuid() });
            services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestUserAuthentication>(SupabaseJwtBearerDefaults.AuthenticationScheme, _ => { });
            services.AddAuthorization();
            services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(MeNotificationsController).Assembly));
                foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray())
                {
                    manager.FeatureProviders.Remove(provider);
                }

                manager.FeatureProviders.Add(new OnlyMeNotificationsController());
            });
        });
        web.Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        });
    }).Start();

    private sealed class StubService : IMeNotificationsService
    {
        public int Updated { get; init; }

        public bool MarkReadCalled { get; private set; }

        public Task<MeNotificationSummaryResponse> GetSummaryAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
            Task.FromResult(new MeNotificationSummaryResponse(0, []));

        public Task<MeNotificationsListResponse> ListAsync(ClaimsPrincipal principal, string? cursor, bool unreadOnly, int limit, string? language, CancellationToken cancellationToken) =>
            Task.FromResult(new MeNotificationsListResponse([], null));

        public Task<int> MarkReadAsync(ClaimsPrincipal principal, MarkNotificationsReadRequest request, CancellationToken cancellationToken)
        {
            MarkReadCalled = true;
            return Task.FromResult(Updated);
        }
    }

    private sealed class TestUserAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Header = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var sub = Request.Headers[Header].ToString();
            if (string.IsNullOrEmpty(sub))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class OnlyMeNotificationsController : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(MeNotificationsController);
    }
}
