using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
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
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingPolicyAuthorizationTests
{
    [Fact]
    public async Task Get_without_booking_policy_read_is_forbidden()
    {
        using var host = StartHost(Permissions.Rentals.ScheduleWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        var response = await client.GetAsync("/api/rentals/booking-policy");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_with_customers_read_is_forbidden()
    {
        using var host = StartHost(Permissions.Core.CustomersRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        var response = await client.GetAsync("/api/rentals/booking-policy");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_with_booking_policy_read_reaches_the_service()
    {
        using var host = StartHost(Permissions.Rentals.BookingPolicyRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "admin@club.test");

        var response = await client.GetAsync("/api/rentals/booking-policy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_with_only_read_is_forbidden()
    {
        using var host = StartHost(Permissions.Rentals.BookingPolicyRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "admin@club.test");

        using var content = JsonBody();
        var response = await client.PutAsync("/api/rentals/booking-policy", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_with_customers_read_is_forbidden()
    {
        using var host = StartHost(Permissions.Core.CustomersRead, Permissions.Rentals.BookingPolicyRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "admin@club.test");

        using var content = JsonBody();
        var response = await client.PutAsync("/api/rentals/booking-policy", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_with_booking_policy_write_reaches_the_service()
    {
        using var host = StartHost(Permissions.Rentals.BookingPolicyWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "admin@club.test");

        using var content = JsonBody();
        var response = await client.PutAsync("/api/rentals/booking-policy", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Put_without_authentication_is_unauthorized()
    {
        using var host = StartHost(Permissions.Rentals.BookingPolicyWrite);
        var client = host.GetTestClient();

        using var content = JsonBody();
        var response = await client.PutAsync("/api/rentals/booking-policy", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Public_get_does_not_require_a_b2b_permission()
    {
        using var host = StartHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/public/tenants/north/booking-policy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static StringContent JsonBody() =>
        new(
            """{"cancellationPolicyText":"Plain note","maxActiveReservationsPerCustomerPerDay":2}""",
            Encoding.UTF8,
            "application/json");

    private static IHost StartHost(params string[] allowedPermissionKeys) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IBookingPolicyService, StubBookingPolicyService>();
                    services.AddSingleton<ITenantProvider, StubTenantProvider>();
                    services.AddSingleton<ITenantModuleAccessor, StubTenantModuleAccessor>();
                    services.AddSingleton<IPublicTenantBinder, StubPublicTenantBinder>();
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(BookingPolicyController).Assembly));
                            manager.FeatureProviders.Add(
                                new SingleControllerFeatureProvider(typeof(BookingPolicyController)));
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
            if (requirement.PermissionKeys.Any(allowedKeys.Contains))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubBookingPolicyService : IBookingPolicyService
    {
        public Task<BookingPolicyResponseDto> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BookingPolicyResponseDto("Plain note", null, null));

        public Task<BookingPolicyResponseDto> UpsertAsync(
            UpsertBookingPolicyRequestDto request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BookingPolicyResponseDto(
                request.CancellationPolicyText,
                request.MaxActiveReservationsPerCustomerPerDay,
                request.MaxAdvanceBookingDays));
    }

    private sealed class StubTenantProvider : ITenantProvider
    {
        public Guid? TenantId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class StubTenantModuleAccessor : ITenantModuleAccessor
    {
        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(
                new HashSet<string>(StringComparer.Ordinal) { PlatformModules.Rentals });
    }

    private sealed class StubPublicTenantBinder : IPublicTenantBinder
    {
        public Task BindFromSubdomainAsync(string? subdomain, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SingleControllerFeatureProvider(Type controllerType) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == controllerType;
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
