using System.Net;
using System.Net.Http.Json;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Units;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Units;

public sealed class UnitsHttpTests
{
    [Fact]
    public async Task Create_requires_units_write_permission()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, Permissions.Core.UnitsRead);

        var response = await client.PostAsJsonAsync("/api/units", new { name = "Clubhouse" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await harness.Db.Units.ToListAsync());
    }

    [Fact]
    public async Task Create_is_tenant_scoped_rejects_duplicate_name_and_returns_list_dto()
    {
        await using var harness = new Harness();
        using var host = StartHost(harness);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthentication.Header, Permissions.Core.UnitsWrite + "," + Permissions.Core.UnitsRead);

        var created = await client.PostAsJsonAsync("/api/units", new { name = " Clubhouse ", code = " MAIN " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<UnitResponse>();
        Assert.NotNull(dto);
        Assert.Equal(harness.FirstTenant, dto.TenantId);
        Assert.Equal("Clubhouse", dto.Name);
        Assert.Equal("MAIN", dto.Code);
        Assert.True(dto.IsActive);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/units", new { name = "Clubhouse" })).StatusCode);

        harness.Provider.TenantId = harness.SecondTenant;
        harness.Db.Units.Add(new Unit(harness.SecondTenant, "Foreign"));
        await harness.Db.SaveChangesAsync();
        harness.Provider.TenantId = harness.FirstTenant;

        var list = await client.GetFromJsonAsync<List<UnitResponse>>("/api/units");
        Assert.NotNull(list);
        Assert.Single(list);
        Assert.Equal("Clubhouse", list[0].Name);
    }

    private static IHost StartHost(Harness harness) => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddSingleton(harness.Db);
            services.AddSingleton<ITenantProvider>(harness.Provider);
            services.AddSingleton<IAuthorizationHandler, HeaderPermissionHandler>();
            services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(SupabaseJwtBearerDefaults.AuthenticationScheme, _ => { });
            services.AddAuthorization(options => options.AddRolvixPolicies());
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(UnitsController).Assembly));
                foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray()) manager.FeatureProviders.Remove(provider);
                manager.FeatureProviders.Add(new UnitsControllerFeatureProvider());
            });
        });
        web.Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseEndpoints(endpoints => endpoints.MapControllers()); });
    }).Start();

    private sealed class Harness : IAsyncDisposable
    {
        public Guid FirstTenant { get; } = Guid.NewGuid();
        public Guid SecondTenant { get; } = Guid.NewGuid();
        public FakeTenantProvider Provider { get; } = new();
        public AppDbContext Db { get; }

        public Harness()
        {
            Provider.TenantId = FirstTenant;
            Db = InMemoryAppDb.Create(Provider);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class HeaderPermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            if (context.User.HasClaim("permission", requirement.PermissionKey)) context.Succeed(requirement);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Header = "X-Permission";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = Request.Headers[Header].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => new Claim("permission", value));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class UnitsControllerFeatureProvider : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(UnitsController);
    }
}
