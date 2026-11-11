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

    [Theory]
    [InlineData(Permissions.Rentals.OccupancyKindsRead)]
    [InlineData(Permissions.Rentals.ScheduleRead)]
    [InlineData(Permissions.Rentals.ScheduleWrite)]
    public async Task Occupancy_kinds_get_with_any_list_permission_returns_ok(string permission)
    {
        using var host = StartHost(permission);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        using var response = await client.GetAsync("/api/occupancy-kinds");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Permissions.Core.CustomersRead)]
    [InlineData(Permissions.Rentals.OccupancyKindsWrite)]
    public async Task Occupancy_kinds_get_without_list_permission_returns_forbidden(string? permission)
    {
        using var host = StartHost(permission is null ? [] : [permission]);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        using var response = await client.GetAsync("/api/occupancy-kinds");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Permissions.Rentals.OccupancyKindsRead)]
    [InlineData(Permissions.Rentals.ScheduleRead)]
    [InlineData(Permissions.Rentals.ScheduleWrite)]
    [InlineData(Permissions.Rentals.OccupancyKindsWrite)]
    public async Task Occupancy_kinds_mutations_require_occupancy_kinds_write(string? permission)
    {
        using var host = StartHost(permission is null ? [] : [permission]);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        using var createContent = new StringContent(
            """{"key":"lesson","label":"Lesson"}""", Encoding.UTF8, "application/json");
        using var updateContent = new StringContent(
            """{"key":"lesson","label":"Lesson"}""", Encoding.UTF8, "application/json");
        using var create = await client.PostAsync("/api/occupancy-kinds", createContent);
        using var update = await client.PutAsync(
            "/api/occupancy-kinds/11111111-1111-1111-1111-111111111111", updateContent);

        var canWrite = permission == Permissions.Rentals.OccupancyKindsWrite;
        Assert.Equal(canWrite ? HttpStatusCode.Created : HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(canWrite ? HttpStatusCode.OK : HttpStatusCode.Forbidden, update.StatusCode);
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
                    services.AddSingleton<IOccupancyKindService, StubOccupancyKindService>();
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
                                new SelectedControllersFeatureProvider(
                                    typeof(BookingPolicyController), typeof(OccupancyKindsController)));
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
        public Task<IReadOnlyList<CategoryBookingPolicyResponseDto>> GetCategoriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CategoryBookingPolicyResponseDto>>([]);

        public Task<CategoryBookingPolicyResponseDto> UpsertCategoryAsync(Guid categoryId,
            UpsertCategoryBookingPolicyRequestDto request, CancellationToken cancellationToken) =>
            Task.FromResult(new CategoryBookingPolicyResponseDto(categoryId, "Category",
                request.MaxAdvanceBookingDays, request.CancelCutoffHours,
                request.MinimumBookingLeadMinutes.IsSpecified ? request.MinimumBookingLeadMinutes.Value : null,
                request.DepositPercent.IsSpecified ? request.DepositPercent.Value : null));

        public Task<BookingPolicyResponseDto> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BookingPolicyResponseDto("Plain note", null, null));

        public Task<BookingPolicyResponseDto> UpsertAsync(
            UpsertBookingPolicyRequestDto request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BookingPolicyResponseDto(
                request.CancellationPolicyText,
                request.MaxActiveReservationsPerCustomerPerDay,
                request.MaxAdvanceBookingDays.IsSpecified
                    ? request.MaxAdvanceBookingDays.Value
                    : null,
                MinimumBookingLeadMinutes: request.MinimumBookingLeadMinutes.IsSpecified
                    ? request.MinimumBookingLeadMinutes.Value
                    : null,
                DepositPercent: request.DepositPercent.IsSpecified
                    ? request.DepositPercent.Value
                    : null));
    }

    private sealed class StubOccupancyKindService : IOccupancyKindService
    {
        public Task EnsureDefaultsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<OccupancyKindResponseDto>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OccupancyKindResponseDto>>([]);

        public Task<OccupancyKindResponseDto> CreateAsync(
            UpsertOccupancyKindRequestDto request, CancellationToken cancellationToken) =>
            Task.FromResult(Response(Guid.NewGuid(), request));

        public Task<OccupancyKindResponseDto> UpdateAsync(
            Guid id, UpsertOccupancyKindRequestDto request, CancellationToken cancellationToken) =>
            Task.FromResult(Response(id, request));

        private static OccupancyKindResponseDto Response(Guid id, UpsertOccupancyKindRequestDto request) =>
            new(id, request.Key, request.Label, request.Description, request.ColorHex, request.IconKey,
                request.IsBookableByCustomer, request.BlocksCapacity, request.SortOrder, request.IsActive);
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

    private sealed class SelectedControllersFeatureProvider(params Type[] controllerTypes) : ControllerFeatureProvider
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
