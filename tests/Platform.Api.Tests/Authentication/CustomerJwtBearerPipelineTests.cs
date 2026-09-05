using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Notifications;
using Platform.Api.Storage;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Authentication;

public sealed class CustomerJwtBearerPipelineTests
{
    private const string CustomerJwtSecret = "customer-jwt-test-secret-key-32bytes!";
    private const string B2BIssuer = "https://example.supabase.co/auth/v1";
    private const string ProductsPath = "/api/catalog/portal/products";
    private const string B2BProbePath = "/api/test/b2b-probe";
    private const string PlatformAdminProbePath = "/api/test/platform-admin-probe";
    private const string AdminEmail = "admin@rolvix.test";

    [Fact]
    public async Task AddSupabaseAuthentication_registers_CustomerJwt_without_oidc_and_keeps_B2B_default()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSupabaseAuthentication(CreateCustomerJwtConfiguration());

        await using var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        var defaultAuthenticate = await schemes.GetDefaultAuthenticateSchemeAsync();
        Assert.Equal(JwtBearerDefaults.AuthenticationScheme, defaultAuthenticate?.Name);

        var bearerScheme = await schemes.GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(bearerScheme);
        Assert.Equal(typeof(PolicySchemeHandler), bearerScheme.HandlerType);

        var supabaseScheme = await schemes.GetSchemeAsync(SupabaseJwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(supabaseScheme);
        Assert.Equal(typeof(JwtBearerHandler), supabaseScheme.HandlerType);

        var customerScheme = await schemes.GetSchemeAsync(CustomerJwtBearerDefaults.AuthenticationScheme);
        Assert.NotNull(customerScheme);
        Assert.Equal(typeof(JwtBearerHandler), customerScheme.HandlerType);

        var jwtOptions = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        var customerOptions = jwtOptions.Get(CustomerJwtBearerDefaults.AuthenticationScheme);
        Assert.False(customerOptions.MapInboundClaims);
        Assert.True(string.IsNullOrEmpty(customerOptions.MetadataAddress));
        Assert.True(string.IsNullOrEmpty(customerOptions.Authority));
        Assert.Null(customerOptions.ConfigurationManager);
        Assert.Equal(CustomerJwtIssuer.Audience, customerOptions.Audience);
        Assert.Equal(CustomerJwtIssuer.Issuer, customerOptions.TokenValidationParameters.ValidIssuer);
        Assert.Equal(CustomerJwtIssuer.Audience, customerOptions.TokenValidationParameters.ValidAudience);
        Assert.Equal(
            [SecurityAlgorithms.HmacSha256],
            customerOptions.TokenValidationParameters.ValidAlgorithms);

        var b2bOptions = jwtOptions.Get(SupabaseJwtBearerDefaults.AuthenticationScheme);
        Assert.False(string.IsNullOrEmpty(b2bOptions.MetadataAddress));
        Assert.Contains("/.well-known/openid-configuration", b2bOptions.MetadataAddress, StringComparison.Ordinal);
        Assert.False(b2bOptions.TokenValidationParameters.ValidateIssuer);
        Assert.False(b2bOptions.TokenValidationParameters.ValidateAudience);

        var authz = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var customerPolicy = authz.GetPolicy("Customer");
        Assert.NotNull(customerPolicy);
        Assert.Equal(
            [CustomerJwtBearerDefaults.AuthenticationScheme],
            customerPolicy.AuthenticationSchemes);
        Assert.DoesNotContain(JwtBearerDefaults.AuthenticationScheme, customerPolicy.AuthenticationSchemes);
        Assert.DoesNotContain(SupabaseJwtBearerDefaults.AuthenticationScheme, customerPolicy.AuthenticationSchemes);
        Assert.Equal(
            [SupabaseJwtBearerDefaults.AuthenticationScheme],
            authz.DefaultPolicy.AuthenticationSchemes);
        Assert.DoesNotContain(
            CustomerJwtBearerDefaults.AuthenticationScheme,
            authz.DefaultPolicy.AuthenticationSchemes);
        Assert.Equal(
            [SupabaseJwtBearerDefaults.AuthenticationScheme],
            authz.GetPolicy(SupabaseAuthenticationExtensions.PlatformAdminPolicy)!.AuthenticationSchemes);
        Assert.DoesNotContain(
            CustomerJwtBearerDefaults.AuthenticationScheme,
            authz.GetPolicy(SupabaseAuthenticationExtensions.PlatformAdminPolicy)!.AuthenticationSchemes);
    }

    [Fact]
    public async Task Valid_customer_jwt_lists_only_that_tenant_products()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.TenantACustomer));

        var response = await client.GetAsync(ProductsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(seed.TenantAProductId, item.GetProperty("id").GetGuid());
        Assert.Equal("Tenant A Ball", item.GetProperty("name").GetString());

        var principalResponse = await client.GetAsync("/api/test/default-auth");
        Assert.Equal(HttpStatusCode.OK, principalResponse.StatusCode);
        using var principalDoc = JsonDocument.Parse(await principalResponse.Content.ReadAsStringAsync());
        Assert.True(principalDoc.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.True(principalDoc.RootElement.GetProperty("isCustomer").GetBoolean());
    }

    [Fact]
    public async Task Tenant_B_token_does_not_return_tenant_A_products()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.TenantBCustomer));

        var response = await client.GetAsync(ProductsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(seed.TenantBProductId, item.GetProperty("id").GetGuid());
        Assert.Equal("Tenant B Ball", item.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Wrong_signature_returns_401()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var token = MintHs256(
            secret: "wrong-customer-jwt-secret-key-32byte!",
            issuer: CustomerJwtIssuer.Issuer,
            audience: CustomerJwtIssuer.Audience,
            claims: CustomerClaims(seed.TenantACustomer));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetProductsStatusAsync(host, token));
    }

    [Fact]
    public async Task Wrong_issuer_returns_401()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var token = MintHs256(
            secret: CustomerJwtSecret,
            issuer: "https://evil.example/issuer",
            audience: CustomerJwtIssuer.Audience,
            claims: CustomerClaims(seed.TenantACustomer));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetProductsStatusAsync(host, token));
    }

    [Fact]
    public async Task Wrong_audience_returns_401()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var token = MintHs256(
            secret: CustomerJwtSecret,
            issuer: CustomerJwtIssuer.Issuer,
            audience: "other-audience",
            claims: CustomerClaims(seed.TenantACustomer));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetProductsStatusAsync(host, token));
    }

    [Fact]
    public async Task Expired_token_beyond_clock_skew_returns_401()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var now = DateTime.UtcNow;
        var token = MintHs256(
            secret: CustomerJwtSecret,
            issuer: CustomerJwtIssuer.Issuer,
            audience: CustomerJwtIssuer.Audience,
            claims: CustomerClaims(seed.TenantACustomer),
            notBefore: now.AddMinutes(-30),
            expires: now.AddMinutes(-5));

        Assert.Equal(HttpStatusCode.Unauthorized, await GetProductsStatusAsync(host, token));
    }

    [Fact]
    public async Task B2B_signed_token_against_customer_endpoint_returns_401()
    {
        using var host = await StartHostAsync();
        var token = MintHs256(
            secret: CustomerJwtSecret,
            issuer: B2BIssuer,
            audience: "authenticated",
            claims: [new Claim("sub", Guid.NewGuid().ToString("N"))]);

        Assert.Equal(HttpStatusCode.Unauthorized, await GetProductsStatusAsync(host, token));
    }

    [Fact]
    public async Task Valid_customer_jwt_against_b2b_only_endpoint_is_rejected()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.TenantACustomer));

        var response = await client.GetAsync(B2BProbePath);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403 from DefaultPolicy, got {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    public async Task Malformed_bearer_token_returns_401_without_throwing(string token)
    {
        using var host = await StartHostAsync();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync(ProductsPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Customer_jwt_with_platform_admin_email_never_reads_null_tenant_id()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var recorder = host.Services.GetRequiredService<TenantIdReadRecorder>();
        recorder.Clear();

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.AdminEmailCustomer));

        var response = await client.GetAsync(ProductsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(seed.TenantAProductId, item.GetProperty("id").GetGuid());

        var reads = recorder.Reads;
        Assert.NotEmpty(reads);
        Assert.All(reads, tenantId => Assert.Equal(seed.AdminEmailCustomer.TenantId, tenantId));
        Assert.DoesNotContain((Guid?)null, reads);
    }

    [Fact]
    public async Task Customer_jwt_with_platform_admin_email_is_rejected_by_platform_admin_policy()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.AdminEmailCustomer));

        var response = await client.GetAsync(PlatformAdminProbePath);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403 from PlatformAdmin policy, got {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task Successful_products_request_never_reads_null_tenant_id()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var recorder = host.Services.GetRequiredService<TenantIdReadRecorder>();
        recorder.Clear();

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.TenantACustomer));

        var response = await client.GetAsync(ProductsPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reads = recorder.Reads;
        Assert.NotEmpty(reads);
        Assert.All(reads, tenantId => Assert.Equal(seed.TenantACustomer.TenantId, tenantId));
        Assert.DoesNotContain((Guid?)null, reads);
    }

    [Fact]
    public async Task Valid_signature_without_customer_role_is_rejected_by_customer_policy()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var token = MintHs256(
            secret: CustomerJwtSecret,
            issuer: CustomerJwtIssuer.Issuer,
            audience: CustomerJwtIssuer.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, seed.TenantACustomer.Id.ToString()),
                new Claim(CustomerClaimTypes.CustomerId, seed.TenantACustomer.Id.ToString()),
                new Claim(CustomerClaimTypes.TenantId, seed.TenantACustomer.TenantId.ToString()),
            ]);

        var status = await GetProductsStatusAsync(host, token);
        // JwtBearer authenticates a valid HS256 Customer token; RequireRole then fails → 403.
        // If the handler treated a missing role as unauthenticated, this would be 401.
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    [Fact]
    public async Task Valid_signature_with_non_customer_role_is_rejected_by_customer_policy()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededCatalog>();
        var token = MintHs256(
            secret: CustomerJwtSecret,
            issuer: CustomerJwtIssuer.Issuer,
            audience: CustomerJwtIssuer.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, seed.TenantACustomer.Id.ToString()),
                new Claim(CustomerClaimTypes.CustomerId, seed.TenantACustomer.Id.ToString()),
                new Claim(CustomerClaimTypes.TenantId, seed.TenantACustomer.TenantId.ToString()),
                new Claim(CustomerClaimTypes.Role, "USER"),
            ]);

        var status = await GetProductsStatusAsync(host, token);
        Assert.Equal(HttpStatusCode.Forbidden, status);
    }

    private static async Task<HttpStatusCode> GetProductsStatusAsync(IHost host, string token)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync(ProductsPath);
        return response.StatusCode;
    }

    private static string IssueCustomerToken(Customer customer)
    {
        var issuer = new CustomerJwtIssuer(CreateCustomerJwtConfiguration());
        return issuer.IssueToken(customer);
    }

    private static IConfiguration CreateCustomerJwtConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:Url"] = "https://example.supabase.co",
                ["Supabase:JwtSecret"] = CustomerJwtSecret,
            })
            .Build();

    private static Claim[] CustomerClaims(Customer customer) =>
    [
        new Claim(JwtRegisteredClaimNames.Sub, customer.Id.ToString()),
        new Claim(CustomerClaimTypes.CustomerId, customer.Id.ToString()),
        new Claim(CustomerClaimTypes.TenantId, customer.TenantId.ToString()),
        new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
        new Claim(ClaimTypes.Role, AuthRoles.Customer),
    ];

    private static string MintHs256(
        string secret,
        string issuer,
        string audience,
        IEnumerable<Claim> claims,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        var now = DateTime.UtcNow;
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: notBefore ?? now,
            expires: expires ?? now.AddHours(1),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<IHost> StartHostAsync()
    {
        var databaseName = $"customer-jwt-{Guid.NewGuid():N}";
        var seed = await SeedCatalogAsync(databaseName);

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(seed);
                    services.AddHttpContextAccessor();
                    services.AddScoped<AmbientTenantContext>();
                    services.AddSingleton<IPlatformAdminChecker>(_ =>
                        new PlatformAdminChecker(Options.Create(new PlatformAdminOptions
                        {
                            Emails = [AdminEmail],
                        })));
                    services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                    services.AddSingleton<TenantIdReadRecorder>();
                    services.AddScoped<HttpContextTenantProvider>();
                    services.AddScoped<ITenantProvider>(sp =>
                        new RecordingTenantProvider(
                            sp.GetRequiredService<HttpContextTenantProvider>(),
                            sp.GetRequiredService<TenantIdReadRecorder>()));
                    services.AddScoped<AppDbContext>(sp =>
                    {
                        var options = new DbContextOptionsBuilder<AppDbContext>()
                            .UseInMemoryDatabase(databaseName)
                            .Options;
                        return new TestAppDbContext(options, sp.GetRequiredService<ITenantProvider>());
                    });
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<ICatalogPortalService, CatalogPortalService>();
                    services.AddSingleton<IStorageProvider, NoOpStorageProvider>();
                    services.AddSingleton(Options.Create(new StorageOptions()));
                    services.AddSingleton<ICatalogNotificationPublisher, NoOpCatalogNotificationPublisher>();
                    services.AddSingleton<INotificationOutboxScheduler, NoOpNotificationOutboxScheduler>();
                    services
                        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddPolicyScheme(JwtBearerDefaults.AuthenticationScheme, "JWT scheme router", options =>
                        {
                            options.ForwardDefaultSelector = context => JwtBearerSchemeSelector.Select(context);
                        })
                        .AddJwtBearer(SupabaseJwtBearerDefaults.AuthenticationScheme, options =>
                        {
                            options.MapInboundClaims = false;
                            options.RequireHttpsMetadata = false;
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateIssuerSigningKey = true,
                                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(CustomerJwtSecret)),
                                ValidateIssuer = false,
                                ValidateAudience = false,
                                ValidateLifetime = true,
                                ClockSkew = TimeSpan.FromMinutes(1),
                                RoleClaimType = CustomerClaimTypes.Role,
                                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                            };
                        })
                        .AddJwtBearer(CustomerJwtBearerDefaults.AuthenticationScheme, options =>
                        {
                            options.MapInboundClaims = false;
                            options.RequireHttpsMetadata = false;
                            options.Audience = CustomerJwtIssuer.Audience;
                            options.TokenValidationParameters =
                                SupabaseAuthenticationExtensions.CreateCustomerJwtTokenValidationParameters(
                                    CustomerJwtSecret);
                        });
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(
                                new AssemblyPart(typeof(CatalogPortalController).Assembly));
                            manager.ApplicationParts.Add(
                                new AssemblyPart(typeof(B2BAuthorizeProbeController).Assembly));
                            manager.FeatureProviders.Add(
                                new ExplicitControllerFeatureProvider(
                                    typeof(CatalogPortalController),
                                    typeof(B2BAuthorizeProbeController),
                                    typeof(PlatformAdminProbeController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.Use(async (ctx, next) =>
                    {
                        if (HttpMethods.IsGet(ctx.Request.Method)
                            && ctx.Request.Path.Equals("/api/test/default-auth", StringComparison.Ordinal))
                        {
                            await ctx.Response.WriteAsJsonAsync(new
                            {
                                authenticated = ctx.User.Identities.Any(identity => identity.IsAuthenticated),
                                isCustomer = ctx.User.IsInRole(AuthRoles.Customer),
                            });
                            return;
                        }

                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

        return host;
    }

    private static async Task<SeededCatalog> SeedCatalogAsync(string databaseName)
    {
        var seedProvider = new Fakes.FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(seedProvider, databaseName);

        var tenantA = new Tenant("Club A", "11111111000191", subdomain: "cluba");
        var tenantB = new Tenant("Club B", "22222222000191", subdomain: "clubb");
        db.Tenants.AddRange(tenantA, tenantB);
        db.TenantModules.AddRange(
            new TenantModule(tenantA.Id, PlatformModules.Catalog, isActive: true),
            new TenantModule(tenantB.Id, PlatformModules.Catalog, isActive: true));

        var customerA = new Customer
        {
            TenantId = tenantA.Id,
            Name = "Ana",
            Email = "ana@club.test",
        };
        var customerB = new Customer
        {
            TenantId = tenantB.Id,
            Name = "Bruno",
            Email = "bruno@club.test",
        };
        var customerAdmin = new Customer
        {
            TenantId = tenantA.Id,
            Name = "Admin Member",
            Email = AdminEmail,
        };
        db.Customers.AddRange(customerA, customerB, customerAdmin);

        var productA = new CatalogProduct
        {
            TenantId = tenantA.Id,
            Name = "Tenant A Ball",
            Price = 10m,
            Currency = "BRL",
            IsActive = true,
        };
        var productB = new CatalogProduct
        {
            TenantId = tenantB.Id,
            Name = "Tenant B Ball",
            Price = 20m,
            Currency = "BRL",
            IsActive = true,
        };
        db.CatalogProducts.AddRange(productA, productB);
        await db.SaveChangesAsync();

        return new SeededCatalog(customerA, customerB, customerAdmin, productA.Id, productB.Id);
    }

    private sealed record SeededCatalog(
        Customer TenantACustomer,
        Customer TenantBCustomer,
        Customer AdminEmailCustomer,
        Guid TenantAProductId,
        Guid TenantBProductId);

    private sealed class ExplicitControllerFeatureProvider(params Type[] controllerTypes)
        : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }

    private sealed class NoOpStorageProvider : IStorageProvider
    {
        public Task UploadAsync(
            string bucket,
            string key,
            Stream stream,
            string contentType,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public string GetPublicUrl(string bucket, string key) => $"https://storage.test/{bucket}/{key}";

        public Task<string> CreateSignedUrlAsync(
            string bucket,
            string key,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"https://storage.test/{bucket}/{key}?sig=test");

        public Task DeleteAsync(
            string bucket,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoOpCatalogNotificationPublisher : ICatalogNotificationPublisher
    {
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> PublishOrderEventAsync(
            CatalogOrder order,
            string eventType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}

internal sealed class TenantIdReadRecorder
{
    private readonly List<Guid?> _reads = [];
    private readonly object _gate = new();

    public IReadOnlyList<Guid?> Reads
    {
        get
        {
            lock (_gate)
            {
                return [.. _reads];
            }
        }
    }

    public void Record(Guid? tenantId)
    {
        lock (_gate)
        {
            _reads.Add(tenantId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _reads.Clear();
        }
    }
}

internal sealed class RecordingTenantProvider(ITenantProvider inner, TenantIdReadRecorder recorder)
    : ITenantProvider
{
    public Guid? TenantId
    {
        get
        {
            var tenantId = inner.TenantId;
            recorder.Record(tenantId);
            return tenantId;
        }
    }
}

[ApiController]
[Authorize]
[Route("api/test/b2b-probe")]
public sealed class B2BAuthorizeProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}

[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/test/platform-admin-probe")]
public sealed class PlatformAdminProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok();
}
