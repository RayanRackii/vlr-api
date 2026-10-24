using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Http;

public sealed class FailClosedTenancyHostTests
{
    [Theory]
    [InlineData("/api/asset-categories")]
    [InlineData("/api/catalog/products")]
    public async Task Staff_reads_own_module_data_and_cannot_fetch_another_tenants_id(string path)
    {
        using var environment = new RateLimitClientIpHostTests.HostEnvironmentScope(("Tenancy__FailClosed", "true"));
        using var factory = new Factory();
        using var client = factory.CreateClient();
        await factory.SeedAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", StaffToken(factory.Alice));
        var ownId = path == "/api/asset-categories" ? factory.CategoryA.Id : factory.ProductA.Id;
        var otherId = path == "/api/asset-categories" ? factory.CategoryB.Id : factory.ProductB.Id;

        using var list = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var rows = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ownId, Assert.Single(rows.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{path}/{ownId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{otherId}")).StatusCode);

        // The hidden row exists and is readable by its owner, through the same host and cached EF model.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", StaffToken(factory.Bob));
        using var otherList = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, otherList.StatusCode);
        var otherRows = await otherList.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(otherId, Assert.Single(otherRows.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Customer_token_is_valid_at_home_but_rejected_on_another_tenants_public_route()
    {
        using var environment = new RateLimitClientIpHostTests.HostEnvironmentScope(("Tenancy__FailClosed", "true"));
        using var factory = new Factory();
        using var client = factory.CreateClient();
        await factory.SeedAsync();
        var token = factory.Services.GetRequiredService<ICustomerJwtIssuer>().IssueToken(factory.CustomerA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Protected positive control proves authentication/session validation succeeded.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/portal/products")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/public/tenants/club-a/rental-assets")).StatusCode);
        using var mismatch = await client.GetAsync("/api/public/tenants/club-b/rental-assets");
        Assert.Equal(HttpStatusCode.Unauthorized, mismatch.StatusCode);
        var error = await mismatch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(error.TryGetProperty("error", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/portal/products")).StatusCode);
    }

    [Fact]
    public async Task Platform_admin_lists_across_tenants_and_deletes_without_a_tenant_session()
    {
        using var environment = new RateLimitClientIpHostTests.HostEnvironmentScope(("Tenancy__FailClosed", "true"));
        using var factory = new Factory();
        using var client = factory.CreateClient();
        await factory.SeedAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("platform-auth", "platform@test.local"));

        async Task<Guid[]> ListedIdsAsync()
        {
            using var response = await client.GetAsync("/api/admin/users");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rows = await response.Content.ReadFromJsonAsync<JsonElement>();
            return rows.EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).OrderBy(x => x).ToArray();
        }

        Assert.Equal(new[] { factory.Alice.Id, factory.Bob.Id }.OrderBy(x => x), await ListedIdsAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/admin/users/{factory.Bob.Id}")).StatusCode);
        Assert.Equal([factory.Alice.Id], await ListedIdsAsync());
        Assert.Equal(factory.Bob.SupabaseAuthId, Assert.Single(factory.Auth.Deleted));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/admin/users/{factory.Bob.Id}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", StaffToken(factory.Alice));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/admin/users/{factory.Alice.Id}")).StatusCode);
    }

    private const string Secret = "test-secret-test-secret-test-secret";
    private static string StaffToken(User user) => Token(user.SupabaseAuthId, user.Email, user.TenantId);

    private static string Token(string subject, string email, Guid? tenantId = null)
    {
        var claims = new List<Claim> { new("sub", subject), new("email", email) };
        if (tenantId.HasValue)
            claims.Add(new Claim("app_metadata", $$"""{"tenant_id":"{{tenantId}}"}"""));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://example.supabase.co/auth/v1", claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256)));
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"fail-closed-host-{Guid.NewGuid():N}";
        public Tenant A { get; } = new("Club A", "11111111000191", subdomain: "club-a");
        public Tenant B { get; } = new("Club B", "22222222000191", subdomain: "club-b");
        public User Alice { get; private set; } = null!;
        public User Bob { get; private set; } = null!;
        public AssetCategory CategoryA { get; private set; } = null!;
        public AssetCategory CategoryB { get; private set; } = null!;
        public CatalogProduct ProductA { get; private set; } = null!;
        public CatalogProduct ProductB { get; private set; } = null!;
        public Customer CustomerA { get; private set; } = null!;
        public RecordingPasswordAuthAdmin Auth { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                // Replace only persistence and external Auth I/O. Keep policies, middleware,
                // tenant resolution, module gates, services and signed JWT validation real.
                services.RemoveAll<AppDbContext>();
                services.AddScoped<AppDbContext>(sp => InMemoryAppDb.Create(
                    sp.GetRequiredService<ITenantProvider>(), _databaseName,
                    failClosed: sp.GetRequiredService<IOptions<TenancyOptions>>().Value.FailClosed));
                services.RemoveAll<ISupabaseAuthAdminClient>();
                services.AddSingleton<ISupabaseAuthAdminClient>(Auth);
                services.RemoveAll<IGoTrueAccessSessionProbe>();
                services.AddSingleton<IGoTrueAccessSessionProbe>(new ActiveSessionProbe());
                services.Configure<PlatformAdminOptions>(o => o.Emails = ["platform@test.local"]);
                services.PostConfigure<JwtBearerOptions>(SupabaseJwtBearerDefaults.AuthenticationScheme, options =>
                {
                    // Offline discovery, while preserving the production signature/lifetime checks and events.
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new());
                });
            });
        }

        public async Task SeedAsync()
        {
            Assert.True(Services.GetRequiredService<IOptions<TenancyOptions>>().Value.FailClosed);
            Alice = new User(A.Id, "auth-a", "Alice", "alice@test.local");
            Bob = new User(B.Id, "auth-b", "Bob", "bob@test.local");
            CategoryA = new AssetCategory { TenantId = A.Id, Name = "Category A" };
            CategoryB = new AssetCategory { TenantId = B.Id, Name = "Category B" };
            ProductA = new CatalogProduct { TenantId = A.Id, Name = "Product A" };
            ProductB = new CatalogProduct { TenantId = B.Id, Name = "Product B" };
            CustomerA = new Customer { TenantId = A.Id, Name = "Customer A", EmailVerifiedAt = DateTimeOffset.UtcNow };
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.AddRange(A, B);
            db.Users.AddRange(Alice, Bob);
            db.Customers.Add(CustomerA);
            db.AssetCategories.AddRange(CategoryA, CategoryB);
            db.CatalogProducts.AddRange(ProductA, ProductB);
            foreach (var user in new[] { Alice, Bob })
            {
                var role = new Role(user.TenantId, SystemRoles.Admin, isSystemRole: true);
                db.Roles.Add(role);
                db.UserRoles.Add(new UserRole(user.Id, role.Id));
                foreach (var module in new[] { PlatformModules.Inventory, PlatformModules.Catalog, PlatformModules.Rentals })
                    db.TenantModules.Add(new TenantModule(user.TenantId, module));
            }
            await db.SaveChangesAsync();
        }
    }

    private sealed class ActiveSessionProbe : IGoTrueAccessSessionProbe
    {
        public Task<GoTrueAccessSessionStatus> ProbeAsync(string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult(GoTrueAccessSessionStatus.Active);
    }
}
