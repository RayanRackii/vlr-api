using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class DailyAvailabilityHttpTests
{
    private const string Secret = "availability-test-secret-longer-than-32-bytes";
    private const string Path = "/api/public/tenants/example/availability/days/2026-10-21";

    [Fact]
    public async Task Valid_other_tenant_customer_jwt_on_route_tenant_is_unauthorized_without_naming_it()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var foreign = new Tenant("Hidden tenant", "22222222000191", subdomain: "hidden");
        h.Db.Add(foreign);
        await h.Db.SaveChangesAsync();
        using var host = StartHost(h);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            new CustomerJwtIssuer(Configuration()).IssueToken(new Customer { TenantId = foreign.Id, Name = "Hidden customer" }));

        var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("example", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreign.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Real_controller_binds_route_tenant_over_hostile_header()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var foreign = new Tenant("Hidden tenant", "22222222000191", subdomain: "hidden");
        var foreignUnit = new Unit(foreign.Id, "Hidden unit");
        h.Db.AddRange(foreign, foreignUnit);
        var foreignResource = await h.AddResourceAsync(foreign, foreignUnit, "Hidden resource");
        using var host = StartHost(h);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Subdomain", "hidden");
        var response = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var resource = Assert.Single(json.RootElement.GetProperty("resources").EnumerateArray());
        Assert.Equal(h.Primary.Id, resource.GetProperty("rentalAssetId").GetGuid());
        Assert.Equal("Available", resource.GetProperty("windows")[0].GetProperty("state").GetString());
        foreach (var privateField in new[] { "reservationId", "customer", "contact", "label", "notes" })
            Assert.DoesNotContain($"\"{privateField}\"", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var external = await client.GetAsync($"{Path}?rentalAssetIds[]={foreignResource.Id}");
        using var empty = JsonDocument.Parse(await external.Content.ReadAsStringAsync());
        Assert.Empty(empty.RootElement.GetProperty("resources").EnumerateArray());
        var selected = await client.GetAsync($"{Path}?rentalAssetIds[]={h.Primary.Id}&quantity=2&durationMinutes=60");
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        using var chosen = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
        Assert.Equal(40, chosen.RootElement.GetProperty("resources")[0].GetProperty("windows")[0].GetProperty("totalAmount").GetDecimal());
    }

    [Theory]
    [InlineData("?quantity=0")] [InlineData("?quantity=abc")] [InlineData("?quantity=1000001")]
    [InlineData("?durationMinutes=-1")] [InlineData("?durationMinutes=oops")]
    [InlineData("?unitId=abc")] [InlineData("?rentalAssetIds=bad")]
    [InlineData("?rentalAssetIds[]=bad")]
    public async Task Invalid_queries_return_stable_400(string query)
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h);
        var response = await host.GetTestClient().GetAsync(Path + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("AVAILABILITY_INVALID_QUERY", body.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("invalid")] [InlineData("2026-02-30")] [InlineData("9999-12-31")]
    public async Task Invalid_dates_return_stable_400(string date)
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h);
        var response = await host.GetTestClient().GetAsync($"/api/public/tenants/example/availability/days/{date}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("AVAILABILITY_INVALID_QUERY", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_tenant_and_inactive_module_follow_the_existing_public_gates()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        using var host = StartHost(h);
        var client = host.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Path.Replace("example", "missing"))).StatusCode);
        (await h.Db.TenantModules.SingleAsync()).Deactivate();
        await h.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Path)).StatusCode);
    }

    internal static IHost StartHost(AvailabilityHarness h) => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddSingleton<IConfiguration>(Configuration());
            services.AddHttpContextAccessor();
            services.AddScoped<AmbientTenantContext>();
            services.AddScoped<PlatformAdminTenantSession>();
            services.AddSingleton<IPlatformAdminChecker>(new FakePlatformAdminChecker());
            services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
            services.AddScoped(sp => InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), h.DatabaseName));
            services.AddScoped<IPublicTenantBinder, PublicTenantBinder>();
            services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
            services.AddSingleton<TimeProvider>(h.Clock);
            services.AddRentalsModule();
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = CustomerJwtIssuer.Issuer,
                    ValidateAudience = true, ValidAudience = CustomerJwtIssuer.Audience,
                    ValidateLifetime = true, ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
                };
            });
            services.AddAuthorization();
            services.AddControllers().AddJsonOptions(options =>
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
                .ConfigureApplicationPartManager(manager =>
                {
                    manager.ApplicationParts.Clear();
                    foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray())
                        manager.FeatureProviders.Remove(provider);
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(DailyAvailabilityController).Assembly));
                    manager.FeatureProviders.Add(new OnlyAvailabilityController());
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
    }).Start();

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Supabase:JwtSecret"] = Secret }).Build();
    private sealed class OnlyAvailabilityController : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(DailyAvailabilityController);
    }
}
