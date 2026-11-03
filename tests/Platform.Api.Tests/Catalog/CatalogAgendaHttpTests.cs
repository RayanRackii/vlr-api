using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogAgendaHttpTests
{
    private static readonly string Range =
        $"from={Uri.EscapeDataString("2026-09-01T00:00:00Z")}&to={Uri.EscapeDataString("2026-09-10T00:00:00Z")}";

    [Fact]
    public async Task Get_agenda_products_without_orders_read_returns_403()
    {
        using var host = StartHost(Permissions.Rentals.ScheduleRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        var response = await client.GetAsync($"/api/catalog/agenda/products?{Range}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_agenda_resources_without_schedule_read_returns_403()
    {
        using var host = StartHost(Permissions.Catalog.OrdersRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        var response = await client.GetAsync($"/api/catalog/agenda/resources?{Range}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00Z", "0001-01-02T00:00:00Z")]
    [InlineData("2026-09-10T00:00:00Z", "2026-09-01T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00Z", "2026-11-01T00:00:00Z")]
    public void Invalid_agenda_range_is_rejected_with_400(string from, string to)
    {
        var result = CatalogAgendaController.ValidateRange(
            DateTimeOffset.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(to, System.Globalization.CultureInfo.InvariantCulture));

        Assert.NotNull(result);
        Assert.Equal(400, result!.StatusCode);
    }

    private static IHost StartHost(params string[] allowedPermissionKeys) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<ICatalogAgendaService, StubAgendaService>();
                    services.AddSingleton<ITenantProvider, StubTenantProvider>();
                    services.AddSingleton<IAuthorizationHandler>(
                        new AllowlistedPermissionHandler(allowedPermissionKeys));
                    services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(CatalogAgendaController).Assembly));
                            manager.FeatureProviders.Add(new SingleControllerFeatureProvider(typeof(CatalogAgendaController)));
                            manager.FeatureProviders.Add(new SingleControllerFeatureProvider(typeof(Platform.Api.Modules.Rentals.Controllers.ResourceAgendaController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

    private sealed class AllowlistedPermissionHandler(IReadOnlyCollection<string> allowedKeys)
        : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            if (allowedKeys.Contains(requirement.PermissionKey))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubAgendaService : ICatalogAgendaService
    {
        public Task<IReadOnlyList<CatalogAgendaItemResponse>> GetProductsAgendaAsync(
            CatalogAgendaQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogAgendaItemResponse>>([]);

        public Task<CatalogAgendaResourcesResponse> GetResourcesAgendaAsync(
            CatalogAgendaQuery query,
            bool includeOrderDetails,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogAgendaResourcesResponse([], []));
    }

    private sealed class StubTenantProvider : ITenantProvider
    {
        public Guid? TenantId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class SingleControllerFeatureProvider(params Type[] controllerTypes)
        : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string HeaderName = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(HeaderName, out var values))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var email = values.ToString();
            if (string.IsNullOrWhiteSpace(email))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            Claim[] claims =
            [
                new("email", email),
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, email),
                new("sub", "test-sub"),
            ];

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
