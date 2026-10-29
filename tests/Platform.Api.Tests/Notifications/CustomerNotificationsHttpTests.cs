using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;
using Platform.Api.Modules.Notifications.Controllers;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Notifications;

public sealed class CustomerNotificationsHttpTests
{
    private const string Root = "/api/me/customer-notifications";
    private const string SigningKey = "customer-notifications-test-signing-key-32bytes";

    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/summary")]
    [InlineData("POST", "/read-all")]
    [InlineData("POST", "/11111111-1111-1111-1111-111111111111/read")]
    public async Task Anonymous_and_staff_tokens_cannot_access_any_customer_operation(string method, string suffix)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        using var host = StartHost(db);
        var client = host.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Root + suffix))).StatusCode);
        Authenticate(client, Guid.NewGuid(), Guid.NewGuid(), staff: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Root + suffix))).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/summary")]
    [InlineData("GET", "")]
    [InlineData("POST", "/read")]
    public async Task Customer_token_cannot_access_staff_inbox(string method, string suffix)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        using var host = StartHost(db);
        var client = host.GetTestClient();
        Authenticate(client, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/me/notifications" + suffix))).StatusCode);
    }

    [Fact]
    public async Task Http_contract_isolates_customer_tenant_and_staff_and_ignores_supplied_identity()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var otherCustomer = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var own = CustomerNotificationsServiceTests.Seed(db, tenant, customer);
        var foreignCustomer = CustomerNotificationsServiceTests.Seed(db, tenant, otherCustomer);
        var foreignTenant = CustomerNotificationsServiceTests.Seed(db, otherTenant, customer);
        var staff = CustomerNotificationsServiceTests.Seed(db, tenant, customer, NotificationRecipientKind.B2BUser);
        await db.SaveChangesAsync();
        using var host = StartHost(db);
        var client = host.GetTestClient();
        Authenticate(client, tenant, customer);

        var summaryResponse = await client.GetAsync(Root + $"/summary?tenantId={otherTenant}&customerId={otherCustomer}");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summaryJson = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, summaryJson.GetProperty("unreadTotal").GetInt32());
        Assert.True(summaryJson.TryGetProperty("unreadTotal", out _));
        Assert.True(summaryJson.TryGetProperty("groups", out _));
        var listResponse = await client.GetAsync(Root + $"?tenantId={otherTenant}&customerId={otherCustomer}");
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(own.Id, Assert.Single(listJson.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.True(listJson.TryGetProperty("items", out _));
        Assert.True(listJson.TryGetProperty("nextCursor", out _));
        foreach (var foreign in new[] { foreignCustomer, foreignTenant, staff })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Root}/{foreign.Id}/read", null)).StatusCode);
            Assert.Null(foreign.ReadAt);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Root}/{Guid.NewGuid()}/read", null)).StatusCode);
        var read = await client.PostAsync($"{Root}/{own.Id}/read", null);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(1, (await read.Content.ReadFromJsonAsync<MarkNotificationsReadResponse>())!.Updated);
        var again = await client.PostAsync($"{Root}/{own.Id}/read", null);
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<MarkNotificationsReadResponse>())!.Updated);
        var all = await client.PostAsJsonAsync(Root + "/read-all", new { tenantId = otherTenant, customerId = otherCustomer });
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.All(new[] { foreignCustomer, foreignTenant, staff }, d => Assert.Null(d.ReadAt));
    }

    [Fact]
    public async Task Read_all_changes_only_callers_unread_deliveries()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        var tenant = Guid.NewGuid();
        var customer = Guid.NewGuid();
        CustomerNotificationsServiceTests.Seed(db, tenant, customer);
        CustomerNotificationsServiceTests.Seed(db, tenant, customer);
        var foreign = CustomerNotificationsServiceTests.Seed(db, tenant, Guid.NewGuid());
        await db.SaveChangesAsync();
        using var host = StartHost(db);
        var client = host.GetTestClient();
        Authenticate(client, tenant, customer);
        var response = await client.PostAsync(Root + "/read-all", null);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<MarkNotificationsReadResponse>())!.Updated);
        Assert.Equal(0, (await client.GetFromJsonAsync<MeNotificationSummaryResponse>(Root + "/summary"))!.UnreadTotal);
        Assert.Empty((await client.GetFromJsonAsync<MeNotificationsListResponse>(Root + "?unreadOnly=true"))!.Items);
        Assert.Null(foreign.ReadAt);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=51")]
    [InlineData("?cursor=bad-cursor")]
    public async Task Invalid_list_parameters_return_400(string query)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());
        using var host = StartHost(db);
        var client = host.GetTestClient();
        Authenticate(client, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Root + query)).StatusCode);
    }

    private static void Authenticate(HttpClient client, Guid tenant, Guid customer, bool staff = false)
    {
        var token = new JwtSecurityToken(
            issuer: staff ? "test.staff" : CustomerJwtIssuer.Issuer,
            audience: staff ? "test.staff" : CustomerJwtIssuer.Audience,
            claims:
            [
                new Claim(CustomerClaimTypes.Role, staff ? "Staff" : AuthRoles.Customer),
                new Claim(CustomerClaimTypes.CustomerId, customer.ToString()),
                new Claim(CustomerClaimTypes.TenantId, tenant.ToString()),
            ],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    private static IHost StartHost(AppDbContext db) => new HostBuilder().ConfigureWebHost(web =>
    {
        web.UseTestServer();
        web.ConfigureServices(services =>
        {
            services.AddSingleton(db);
            services.AddScoped<ICustomerNotificationsService, CustomerNotificationsService>();
            services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(CustomerJwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = SupabaseAuthenticationExtensions.CreateCustomerJwtTokenValidationParameters(SigningKey);
                })
                .AddJwtBearer(SupabaseJwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = "test.staff", ValidAudience = "test.staff",
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
                    };
                });
            services.AddAuthorization(options => options.AddRolvixPolicies());
            services.AddControllers().ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new AssemblyPart(typeof(CustomerNotificationsController).Assembly));
                foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray())
                {
                    manager.FeatureProviders.Remove(provider);
                }

                manager.FeatureProviders.Add(new InboxControllers());
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

    private sealed class InboxControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            typeInfo.AsType() == typeof(CustomerNotificationsController) || typeInfo.AsType() == typeof(MeNotificationsController);
    }
}
