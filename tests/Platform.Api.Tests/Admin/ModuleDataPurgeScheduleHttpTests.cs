using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Tests.Fakes;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// HTTP contract of <c>POST</c> (schedule) and <c>DELETE</c> (cancel) on
/// <c>/api/admin/tenants/{id}/modules/{module}/data-purge</c>: platform admin only, and how the service
/// outcomes map to status codes. The service itself runs on PostgreSQL in
/// <see cref="ModuleDataPurgeSchedulePostgresTests"/>.
/// </summary>
public sealed class ModuleDataPurgeScheduleHttpTests
{
    private const string AdminEmail = "admin@rolvix.test";

    private static string Url(Guid tenantId, string module = "rentals") =>
        $"/api/admin/tenants/{tenantId}/modules/{module}/data-purge";

    private static StringContent Body(string? confirm) =>
        new(JsonSerializer.Serialize(new { confirm }), Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_tenant_admin_cannot_schedule_or_cancel()
    {
        var service = new StubPurgeService();
        using var host = StartHost(service);
        var url = Url(Guid.NewGuid());

        using var anonymous = host.GetTestClient();
        using var anonymousPost = await anonymous.PostAsync(url, Body("rentals"));
        Assert.Contains(anonymousPost.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });

        using var tenantAdmin = host.GetTestClient();
        tenantAdmin.DefaultRequestHeaders.Add("X-Test-Email", "owner@club.test");
        using var post = await tenantAdmin.PostAsync(url, Body("rentals"));
        using var delete = await tenantAdmin.DeleteAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Scheduling_returns_201_and_passes_the_confirmation_and_the_actor()
    {
        var tenantId = Guid.NewGuid();
        var scheduledFor = DateTimeOffset.UtcNow.AddDays(30);
        var service = new StubPurgeService
        {
            OnSchedule = (_, module) => new ModuleDataPurgeScheduledDto(
                Guid.NewGuid(), module ?? "", DateTimeOffset.UtcNow, scheduledFor),
        };
        using var host = StartHost(service);
        using var client = AdminClient(host);
        client.DefaultRequestHeaders.Add("X-Test-Sub", "auth-sub-1");

        using var response = await client.PostAsync(Url(tenantId), Body("rentals"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rentals", body.GetProperty("module").GetString());
        Assert.Equal(scheduledFor, body.GetProperty("scheduledFor").GetDateTimeOffset());
        Assert.Equal(tenantId, service.LastTenantId);
        Assert.Equal("rentals", service.LastConfirm);
        Assert.Equal("auth-sub-1", service.LastActor);
    }

    [Fact]
    public async Task Blockers_answer_409_with_the_preview_body()
    {
        var preview = new ModuleDataPurgePreviewDto(
            "rentals",
            ModuleActive: false,
            [new ModuleDataPurgeTableDto("rentals.reservations", 2)],
            StorageObjects: 0,
            [new ModuleDataPurgeBlockerDto(ModuleDataPurgeService.ForeignKeyRestrictCode, "catalog.order_item_resource_allocations", 1)]);
        var service = new StubPurgeService { OnSchedule = (_, _) => throw new ModuleDataPurgeBlockedException(preview) };
        using var host = StartHost(service);
        using var client = AdminClient(host);

        using var response = await client.PostAsync(Url(Guid.NewGuid()), Body("rentals"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rentals", body.GetProperty("module").GetString());
        var blocker = Assert.Single(body.GetProperty("blockers").EnumerateArray());
        Assert.Equal("fk.restrict", blocker.GetProperty("code").GetString());
        Assert.Equal("catalog.order_item_resource_allocations", blocker.GetProperty("table").GetString());
        Assert.Equal(2, body.GetProperty("tables")[0].GetProperty("rows").GetInt64());
    }

    [Fact]
    public async Task A_second_schedule_answers_409_with_a_stable_code()
    {
        var service = new StubPurgeService
        {
            OnSchedule = (_, _) => throw new ModuleDataPurgeConflictException(
                ModuleDataPurgeService.AlreadyPendingCode, "A purge is already scheduled for this module."),
        };
        using var host = StartHost(service);
        using var client = AdminClient(host);

        using var response = await client.PostAsync(Url(Guid.NewGuid()), Body("rentals"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("purge.already_pending", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_bad_confirmation_or_module_is_a_400_and_an_unknown_tenant_a_404()
    {
        var service = new StubPurgeService
        {
            OnSchedule = (tenantId, module) =>
            {
                if (module == "bogus")
                    throw new ModuleDataPurgeRequestException("module.unknown", "Unknown module.");
                if (tenantId == Guid.Empty)
                    throw new KeyNotFoundException("Tenant not found.");
                throw new ModuleDataPurgeRequestException("confirm.mismatch", "Type the module key to confirm the purge.");
            },
        };
        using var host = StartHost(service);
        using var client = AdminClient(host);

        using var mismatch = await client.PostAsync(Url(Guid.NewGuid()), Body("nope"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal(
            "confirm.mismatch",
            (await mismatch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var unknownModule = await client.PostAsync(Url(Guid.NewGuid(), "bogus"), Body("bogus"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownModule.StatusCode);

        using var unknownTenant = await client.PostAsync(Url(Guid.Empty), Body("rentals"));
        Assert.Equal(HttpStatusCode.NotFound, unknownTenant.StatusCode);
    }

    [Fact]
    public async Task Cancelling_returns_204_and_passes_the_optional_reason_and_the_actor()
    {
        var tenantId = Guid.NewGuid();
        var service = new StubPurgeService();
        using var host = StartHost(service);
        using var client = AdminClient(host);
        client.DefaultRequestHeaders.Add("X-Test-Sub", "auth-sub-2");

        using var withReason = await client.DeleteAsync(Url(tenantId) + "?reason=kept%20by%20mistake");
        Assert.Equal(HttpStatusCode.NoContent, withReason.StatusCode);
        Assert.Equal("kept by mistake", service.LastReason);
        Assert.Equal("auth-sub-2", service.LastActor);

        using var withoutReason = await client.DeleteAsync(Url(tenantId));
        Assert.Equal(HttpStatusCode.NoContent, withoutReason.StatusCode);
        Assert.Null(service.LastReason);
    }

    [Fact]
    public async Task Cancelling_without_a_pending_purge_is_a_404_and_a_too_long_reason_a_400()
    {
        var service = new StubPurgeService
        {
            OnCancel = reason =>
            {
                if (reason is { Length: > 500 })
                    throw new ModuleDataPurgeRequestException("reason.too_long", "Too long.");
                throw new KeyNotFoundException("No pending purge for this module.");
            },
        };
        using var host = StartHost(service);
        using var client = AdminClient(host);

        using var missing = await client.DeleteAsync(Url(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var tooLong = await client.DeleteAsync(Url(Guid.NewGuid()) + "?reason=" + new string('x', 501));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    private static HttpClient AdminClient(IHost host)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Email", AdminEmail);
        return client;
    }

    private static IHost StartHost(StubPurgeService service) => new HostBuilder()
        .ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton<IModuleDataPurgeService>(service);
                services.AddSingleton<IPlatformAdminChecker>(new FakePlatformAdminChecker(AdminEmail));
                services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options => options.AddPolicy(
                    SupabaseAuthenticationExtensions.PlatformAdminPolicy,
                    policy => policy.Requirements.Add(new PlatformAdminRequirement())));
                services.AddControllers().ConfigureApplicationPartManager(manager =>
                {
                    manager.ApplicationParts.Clear();
                    manager.FeatureProviders.Clear();
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(AdminModuleDataPurgeController).Assembly));
                    manager.FeatureProviders.Add(new PurgeControllerFeatureProvider());
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

    private sealed class PurgeControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(AdminModuleDataPurgeController);
    }

    private sealed class StubPurgeService : IModuleDataPurgeService
    {
        public int Calls { get; private set; }

        public Guid? LastTenantId { get; private set; }

        public string? LastConfirm { get; private set; }

        public string? LastActor { get; private set; }

        public string? LastReason { get; private set; }

        public Func<Guid, string?, ModuleDataPurgeScheduledDto>? OnSchedule { get; init; }

        public Action<string?>? OnCancel { get; init; }

        public Task<ModuleDataPurgePreviewDto> PreviewAsync(Guid tenantId, string? module, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ModuleDataPurgeScheduledDto> ScheduleAsync(
            Guid tenantId, string? module, string? confirm, string? actor, CancellationToken cancellationToken)
        {
            Calls++;
            LastTenantId = tenantId;
            LastConfirm = confirm;
            LastActor = actor;
            return Task.FromResult(OnSchedule?.Invoke(tenantId, module)
                ?? new ModuleDataPurgeScheduledDto(Guid.NewGuid(), module ?? "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30)));
        }

        public Task CancelAsync(
            Guid tenantId, string? module, string? reason, string? actor, CancellationToken cancellationToken)
        {
            Calls++;
            LastTenantId = tenantId;
            LastReason = reason;
            LastActor = actor;
            OnCancel?.Invoke(reason);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Email", out var email))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new("email", email.ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Sub", out var sub))
                claims.Add(new Claim("sub", sub.ToString()));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
