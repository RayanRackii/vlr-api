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
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class TeacherLessonsAuthorizationTests
{
    [Fact]
    public async Task Post_lessons_with_only_broad_schedule_write_is_forbidden()
    {
        using var host = StartHost(Permissions.Rentals.ScheduleWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        using var content = new StringContent(
            """{"rentalAssetId":"11111111-1111-1111-1111-111111111111","date":"2026-08-25","startTime":"10:00:00","endTime":"11:00:00"}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/schedule/lessons", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_lessons_remove_with_only_broad_schedule_write_is_forbidden()
    {
        using var host = StartHost(Permissions.Rentals.ScheduleWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");

        using var content = new StringContent(
            """{"rentalAssetId":"11111111-1111-1111-1111-111111111111","date":"2026-08-25","startTime":"10:00:00","endTime":"11:00:00"}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/schedule/lessons/remove", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_lessons_with_lessons_write_reaches_service()
    {
        using var host = StartHost(Permissions.Rentals.ScheduleLessonsWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "teacher@club.test");

        using var content = new StringContent(
            """{"rentalAssetId":"11111111-1111-1111-1111-111111111111","date":"2026-08-25","startTime":"10:00:00","endTime":"11:00:00"}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/schedule/lessons", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Post_lessons_remove_with_lessons_write_reaches_service()
    {
        using var host = StartHost(Permissions.Rentals.ScheduleLessonsWrite);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "teacher@club.test");

        using var content = new StringContent(
            """{"rentalAssetId":"11111111-1111-1111-1111-111111111111","date":"2026-08-25","startTime":"10:00:00","endTime":"11:00:00"}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/schedule/lessons/remove", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Post_lessons_without_either_write_permission_returns_403()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "member@club.test");

        using var content = new StringContent(
            """{"rentalAssetId":"11111111-1111-1111-1111-111111111111","date":"2026-08-25","startTime":"10:00:00","endTime":"11:00:00"}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/schedule/lessons", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_lessons_remove_without_either_write_permission_returns_403()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "member@club.test");

        using var content = new StringContent(
            """{"rentalAssetId":"11111111-1111-1111-1111-111111111111","date":"2026-08-25","startTime":"10:00:00","endTime":"11:00:00"}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/schedule/lessons/remove", content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_lessons_unauthenticated_returns_401()
    {
        using var host = StartHost(Permissions.Rentals.ScheduleLessonsWrite);
        var client = host.GetTestClient();

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/schedule/lessons", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static IHost StartHost(params string[] allowedPermissionKeys) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<ITeacherLessonService, StubTeacherLessonService>();
                    services.AddSingleton<ITenantProvider, StubTenantProvider>();
                    services.AddSingleton<ITenantModuleAccessor, StubTenantModuleAccessor>();
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(TeacherLessonsController).Assembly));
                            manager.FeatureProviders.Add(
                                new SingleControllerFeatureProvider(typeof(TeacherLessonsController)));
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

    private sealed class StubTeacherLessonService : ITeacherLessonService
    {
        public Task<SlotResponseDto> CreateAsync(
            CreateTeacherLessonRequestDto request,
            CancellationToken cancellationToken) =>
            Task.FromResult(DummySlot());

        public Task<SlotResponseDto> RemoveAsync(
            RemoveTeacherLessonRequestDto request,
            CancellationToken cancellationToken) =>
            Task.FromResult(DummySlot());

        private static SlotResponseDto DummySlot() =>
            new(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "Quadra 1",
                new DateOnly(2026, 8, 25),
                new TimeOnly(10, 0),
                new TimeOnly(11, 0),
                Guid.NewGuid(),
                "lesson",
                "Lesson",
                "#3B82F6",
                false,
                null,
                SlotStatus.Available,
                null,
                false,
                SlotOccurrenceSource.DailyOverride,
                Guid.NewGuid(),
                SchedulePolicy.SlotGrid,
                true);
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
