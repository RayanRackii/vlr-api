using Platform.Core.Domain.Events;
using Platform.Api.DomainEvents;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Notifications;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Catalog;

public sealed class PortalProductRequestReadTests
{
    private const string CustomerJwtSecret = "customer-jwt-test-secret-key-32bytes!";
    private const string ListPath = "/api/catalog/portal/product-requests";

    private static readonly DateTimeOffset NewestAt = new(2026, 1, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MiddleAt = new(2026, 1, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OldestAt = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OtherTenantAt = new(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Customer_lists_own_product_requests_newest_first_with_file_url()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededRequests>();

        using var firstPage = await ReadJsonAsync(host, seed.Ana, $"{ListPath}?page=1&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, firstPage.Status);
        Assert.Equal(1, firstPage.Document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, firstPage.Document.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, firstPage.Document.RootElement.GetProperty("total").GetInt32());

        var firstItems = firstPage.Document.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, firstItems.Count);
        AssertRequest(firstItems[0], seed.NewestId, "Seals", 3, NewestAt, fileName: null, urlPrefix: null);
        AssertRequest(
            firstItems[1],
            seed.MiddleId,
            "Gaskets",
            2,
            MiddleAt,
            "gasket.pdf",
            seed.MiddleFileUrlPrefix);

        using var secondPage = await ReadJsonAsync(host, seed.Ana, $"{ListPath}?page=2&pageSize=2");
        Assert.Equal(HttpStatusCode.OK, secondPage.Status);
        Assert.Equal(3, secondPage.Document.RootElement.GetProperty("total").GetInt32());
        var secondItems = secondPage.Document.RootElement.GetProperty("items").EnumerateArray().ToList();
        var oldest = Assert.Single(secondItems);
        AssertRequest(oldest, seed.OldestId, "Bolts", 1, OldestAt, fileName: null, urlPrefix: null);

        using var defaults = await ReadJsonAsync(host, seed.Ana, ListPath);
        Assert.Equal(HttpStatusCode.OK, defaults.Status);
        Assert.Equal(1, defaults.Document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, defaults.Document.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, defaults.Document.RootElement.GetProperty("total").GetInt32());
        var all = defaults.Document.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([seed.NewestId, seed.MiddleId, seed.OldestId], all);

        using var detail = await ReadJsonAsync(host, seed.Ana, $"{ListPath}/{seed.MiddleId}");
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        AssertRequest(
            detail.Document.RootElement,
            seed.MiddleId,
            "Gaskets",
            2,
            MiddleAt,
            "gasket.pdf",
            seed.MiddleFileUrlPrefix);
    }

    [Fact]
    public async Task Other_customer_and_other_tenant_cannot_read_product_requests()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededRequests>();

        await AssertHiddenAsync(host, seed.Bruno, seed);
        await AssertHiddenAsync(host, seed.Carla, seed);

        using var diegoList = await ReadJsonAsync(host, seed.Diego, ListPath);
        Assert.Equal(HttpStatusCode.OK, diegoList.Status);
        Assert.Equal(1, diegoList.Document.RootElement.GetProperty("total").GetInt32());
        var diegoItem = Assert.Single(diegoList.Document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(seed.DiegoRequestId, diegoItem.GetProperty("id").GetGuid());
        Assert.Equal("Other tenant part", diegoItem.GetProperty("description").GetString());
        Assert.Equal(0, diegoItem.GetProperty("files").GetArrayLength());

        using var anaSeesDiego = await GetAsync(host, seed.Ana, $"{ListPath}/{seed.DiegoRequestId}");
        Assert.Equal(HttpStatusCode.NotFound, anaSeesDiego.StatusCode);
        Assert.DoesNotContain("Other tenant part", await anaSeesDiego.Content.ReadAsStringAsync());

        using var diegoSeesAna = await GetAsync(host, seed.Diego, $"{ListPath}/{seed.NewestId}");
        Assert.Equal(HttpStatusCode.NotFound, diegoSeesAna.StatusCode);
        Assert.DoesNotContain("Seals", await diegoSeesAna.Content.ReadAsStringAsync());

        using var missing = await GetAsync(host, seed.Ana, $"{ListPath}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Invalid_product_request_page_is_rejected_and_a_far_page_is_empty()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededRequests>();

        using var zeroPage = await ReadJsonAsync(host, seed.Ana, $"{ListPath}?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, zeroPage.Status);
        Assert.Equal("Page must be at least 1.", zeroPage.Document.RootElement.GetProperty("error").GetString());

        using var hugePageSize = await ReadJsonAsync(host, seed.Ana, $"{ListPath}?pageSize=51");
        Assert.Equal(HttpStatusCode.BadRequest, hugePageSize.Status);
        Assert.Equal(
            "Page size must be between 1 and 50.",
            hugePageSize.Document.RootElement.GetProperty("error").GetString());

        using var farPage = await ReadJsonAsync(host, seed.Ana, $"{ListPath}?page={int.MaxValue}&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, farPage.Status);
        Assert.Equal(int.MaxValue, farPage.Document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(3, farPage.Document.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(farPage.Document.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Anonymous_product_request_reads_are_unauthorized()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededRequests>();
        var client = host.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ListPath)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync($"{ListPath}/{seed.NewestId}")).StatusCode);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "")]
    [InlineData(false, "   ")]
    [InlineData(false, " entrega ")]
    [InlineData(false, 1000)]
    [InlineData(false, 1001)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    [InlineData(true, " entrega ")]
    [InlineData(true, 1000)]
    [InlineData(true, 1001)]
    public async Task Customer_note_limit_is_enforced_over_http(bool order, object? input)
    {
        var note = input is int length ? new string('a', length) : (string?)input;
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededRequests>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.Ana));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var beforeOrders = await db.CatalogOrders.IgnoreQueryFilters().CountAsync();
        var beforeRequests = await db.ProductRequests.IgnoreQueryFilters().CountAsync();
        HttpResponseMessage response;
        if (order)
        {
            var product = new CatalogProduct { TenantId = seed.Ana.TenantId, Name = "Item" };
            db.CatalogProducts.Add(product);
            await db.SaveChangesAsync();
            response = await client.PostAsJsonAsync("/api/catalog/portal/orders", new CreatePortalOrderRequest
            {
                Items = [new CreatePortalOrderItemRequest { ProductId = product.Id, Quantity = 1 }],
                CustomerNote = note,
            });
        }
        else
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Item solicitado"), "description");
            form.Add(new StringContent("1"), "quantity");
            if (note is not null)
            {
                form.Add(new StringContent(note), "note");
            }

            response = await client.PostAsync(ListPath, form);
        }

        using (response)
        using (var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            if (note?.Length > 1000)
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("A observação deve ter no máximo 1000 caracteres.",
                    body.RootElement.GetProperty("error").GetString());
                Assert.Equal(beforeOrders, await db.CatalogOrders.IgnoreQueryFilters().CountAsync());
                Assert.Equal(beforeRequests, await db.ProductRequests.IgnoreQueryFilters().CountAsync());
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var expected = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
                Assert.Equal(expected, body.RootElement.GetProperty(order ? "customerNote" : "note").GetString());
                if (order)
                {
                    Assert.Equal(expected, (await db.CatalogOrders.IgnoreQueryFilters().SingleAsync()).CustomerNote);
                }
                else
                {
                    var id = body.RootElement.GetProperty("id").GetGuid();
                    Assert.Equal(expected, (await db.ProductRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == id)).Note);
                    using var detail = await ReadJsonAsync(host, seed.Ana, $"{ListPath}/{id}");
                    Assert.Equal(expected, detail.Document.RootElement.GetProperty("note").GetString());
                }
            }
        }
    }

    [Fact]
    public async Task Customer_request_cancel_enforces_policy_and_hides_other_owners_over_http()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededRequests>();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.Ana));
        using var denied = await client.PostAsync($"{ListPath}/{seed.NewestId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        using var deniedBody = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Contains("não permite", deniedBody.RootElement.GetProperty("error").GetString());

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CatalogPolicies.Add(new CatalogPolicy { TenantId = seed.Ana.TenantId, CustomerCanCancelRequests = true });
            await db.SaveChangesAsync();
        }
        using var allowed = await client.PostAsync($"{ListPath}/{seed.NewestId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var body = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
        Assert.Equal("Cancelled", body.RootElement.GetProperty("status").GetString());
        using var repeated = await client.PostAsync($"{ListPath}/{seed.NewestId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.Bruno));
        using var otherOwner = await client.PostAsync($"{ListPath}/{seed.MiddleId}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, otherOwner.StatusCode);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.Carla));
        using var otherTenant = await client.PostAsync($"{ListPath}/{seed.MiddleId}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await client.PostAsync($"{ListPath}/{seed.MiddleId}/cancel", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Portal_policy_requires_customer_and_uses_customer_claim_tenant()
    {
        using var host = await StartHostAsync();
        var seed = host.Services.GetRequiredService<SeededRequests>();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CatalogPolicies.Add(new CatalogPolicy { TenantId = seed.Ana.TenantId,
                CustomerCanCancelOrders = false, CancellationPolicyText = "Tenant A note" });
            await db.SaveChangesAsync();
        }
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TenantHeaders.Subdomain, "cluba");
        using var anonymous = await client.GetAsync("/api/catalog/portal/policy");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Remove(TenantHeaders.Subdomain);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.Ana));
        using var response = await client.GetAsync("/api/catalog/portal/policy");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("canCancelOrders").GetBoolean());
        Assert.Equal("Tenant A note", body.RootElement.GetProperty("policyText").GetString());
        Assert.Equal("Requested", body.RootElement.GetProperty("upToStatus").GetProperty("orders").GetString());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(seed.Carla));
        using var customerResponse = await client.GetAsync("/api/catalog/portal/policy");
        Assert.Equal(HttpStatusCode.OK, customerResponse.StatusCode);
        using var customerBody = JsonDocument.Parse(await customerResponse.Content.ReadAsStringAsync());
        Assert.True(customerBody.RootElement.GetProperty("canCancelOrders").GetBoolean());
        Assert.False(customerBody.RootElement.GetProperty("canCancelRequests").GetBoolean());
        Assert.Equal(JsonValueKind.Null, customerBody.RootElement.GetProperty("policyText").ValueKind);
    }

    private static async Task AssertHiddenAsync(IHost host, Customer customer, SeededRequests seed)
    {
        using var list = await ReadJsonAsync(host, customer, ListPath);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(0, list.Document.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(list.Document.RootElement.GetProperty("items").EnumerateArray());

        foreach (var id in new[] { seed.NewestId, seed.MiddleId, seed.OldestId })
        {
            using var response = await GetAsync(host, customer, $"{ListPath}/{id}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Seals", body);
            Assert.DoesNotContain("Gaskets", body);
            Assert.DoesNotContain("Bolts", body);
        }
    }

    private static void AssertRequest(
        JsonElement item,
        Guid id,
        string description,
        int quantity,
        DateTimeOffset createdAt,
        string? fileName,
        string? urlPrefix)
    {
        Assert.Equal(id, item.GetProperty("id").GetGuid());
        Assert.Equal("Submitted", item.GetProperty("status").GetString());
        Assert.Equal(description, item.GetProperty("description").GetString());
        Assert.Equal(quantity, item.GetProperty("quantity").GetInt32());
        Assert.Equal(createdAt, item.GetProperty("createdAt").GetDateTimeOffset());
        Assert.False(item.TryGetProperty("customerId", out _));
        Assert.Equal(description == "Gaskets" ? "Entregar na recepção" : null,
            item.GetProperty("note").GetString());
        Assert.False(item.TryGetProperty("storageKey", out _));

        var files = item.GetProperty("files").EnumerateArray().ToList();
        if (fileName is null)
        {
            Assert.Empty(files);
            return;
        }

        var file = Assert.Single(files);
        Assert.Equal(fileName, file.GetProperty("fileName").GetString());
        Assert.False(file.TryGetProperty("storageKey", out _));
        var url = file.GetProperty("url").GetString();
        Assert.NotNull(url);
        Assert.StartsWith(urlPrefix, url, StringComparison.Ordinal);
        Assert.Contains("sig=dev", url, StringComparison.Ordinal);
        Assert.Contains("catalog-private", url, StringComparison.Ordinal);
    }

    private static async Task<JsonResponse> ReadJsonAsync(IHost host, Customer customer, string path)
    {
        using var response = await GetAsync(host, customer, path);
        var body = await response.Content.ReadAsStringAsync();
        return new JsonResponse(response.StatusCode, JsonDocument.Parse(body));
    }

    private static async Task<HttpResponseMessage> GetAsync(IHost host, Customer customer, string path)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", IssueCustomerToken(customer));
        return await client.GetAsync(path);
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

    private static async Task<IHost> StartHostAsync()
    {
        var databaseName = $"portal-product-requests-{Guid.NewGuid():N}";
        var seed = await SeedAsync(databaseName);

        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(seed);
                    services.AddHttpContextAccessor();
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddSingleton<IPlatformAdminChecker>(_ =>
                        new PlatformAdminChecker(Options.Create(new PlatformAdminOptions
                        {
                            Emails = ["admin@rolvix.test"],
                        })));
                    services.AddScoped<HttpContextTenantProvider>();
                    services.AddScoped<ITenantProvider>(sp => sp.GetRequiredService<HttpContextTenantProvider>());
                    services.AddScoped<AppDbContext>(sp =>
                    {
                        var options = new DbContextOptionsBuilder<AppDbContext>()
                            .UseInMemoryDatabase(databaseName)
                            .Options;
                        return new TestAppDbContext(options, sp.GetRequiredService<ITenantProvider>());
                    });
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddSingleton<IHostEnvironment, FakeHostEnvironment>();
                    services.AddSingleton(Options.Create(new StorageOptions()));
                    services.AddSingleton<IStorageProvider, DevStorageProvider>();
                    services.AddSingleton<ICatalogNotificationPublisher, NoOpCatalogNotificationPublisher>();
                    services.AddScoped<IDomainEventDispatcher<CatalogOrderEvent>>(sp =>
                        sp.GetRequiredService<ICatalogNotificationPublisher>().AsDomainEvents());
                    services.AddSingleton<INotificationOutboxScheduler, NoOpNotificationOutboxScheduler>();
                    services.AddScoped<ICatalogPortalService, CatalogPortalService>();
                    services.AddScoped<CatalogPolicyService>();
                    services.AddScoped<IPublicTenantBinder, PublicTenantBinder>();
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
                                IssuerSigningKey = new SymmetricSecurityKey(
                                    System.Text.Encoding.UTF8.GetBytes(CustomerJwtSecret)),
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
                        .AddJsonOptions(options =>
                        {
                            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                        })
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(
                                new AssemblyPart(typeof(CatalogPortalController).Assembly));
                            manager.FeatureProviders.Add(
                                new ExplicitControllerFeatureProvider(typeof(CatalogPortalController), typeof(CatalogPolicyController)));
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
    }

    private static async Task<SeededRequests> SeedAsync(string databaseName)
    {
        var seedProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(seedProvider, databaseName);

        var tenantA = new Tenant("Club A", "11111111000191", subdomain: "cluba");
        var tenantB = new Tenant("Club B", "22222222000191", subdomain: "clubb");
        db.Tenants.AddRange(tenantA, tenantB);
        db.TenantModules.AddRange(
            new TenantModule(tenantA.Id, PlatformModules.Catalog, isActive: true),
            new TenantModule(tenantB.Id, PlatformModules.Catalog, isActive: true));

        var ana = NewCustomer(tenantA.Id, "Ana", "ana@club.test");
        var bruno = NewCustomer(tenantA.Id, "Bruno", "bruno@club.test");
        var carla = NewCustomer(tenantB.Id, "Carla", "carla@club.test");
        var diego = NewCustomer(tenantB.Id, "Diego", "diego@club.test");
        db.Customers.AddRange(ana, bruno, carla, diego);

        var newest = Request(tenantA.Id, ana.Id, "Seals", 3);
        var middle = Request(tenantA.Id, ana.Id, "Gaskets", 2);
        middle.Note = "Entregar na recepção";
        var oldest = Request(tenantA.Id, ana.Id, "Bolts", 1);
        var diegoRequest = Request(tenantB.Id, diego.Id, "Other tenant part", 4);
        var file = new ProductRequestFile
        {
            TenantId = tenantA.Id,
            ProductRequestId = middle.Id,
            StorageKey = "pending",
            FileName = "gasket.pdf",
            MimeType = "application/pdf",
            SizeBytes = 128,
            Visibility = CatalogFileVisibility.InternalB2B,
        };
        file.StorageKey = CatalogFileRules.StorageKey(tenantA.Id, middle.Id, file.Id);
        middle.AddFile(file);

        db.ProductRequests.AddRange(newest, middle, oldest, diegoRequest);
        Stamp(db, newest, NewestAt);
        Stamp(db, middle, MiddleAt);
        Stamp(db, oldest, OldestAt);
        Stamp(db, diegoRequest, OtherTenantAt);
        await db.SaveChangesAsync();

        var urlPrefix =
            $"https://dev-storage.local/{Uri.EscapeDataString("catalog-private")}/{file.StorageKey}";
        return new SeededRequests(ana, bruno, carla, diego, newest.Id, middle.Id, oldest.Id, diegoRequest.Id, urlPrefix);
    }

    private static Customer NewCustomer(Guid tenantId, string name, string email) =>
        new()
        {
            TenantId = tenantId,
            Name = name,
            Email = email,
        };

    private static ProductRequest Request(Guid tenantId, Guid customerId, string description, int quantity) =>
        new()
        {
            TenantId = tenantId,
            CustomerId = customerId,
            Description = description,
            Quantity = quantity,
            Status = ProductRequestStatus.Submitted,
        };

    private static void Stamp(AppDbContext db, Entity entity, DateTimeOffset createdAt)
    {
        db.Entry(entity).Property(nameof(Entity.CreatedAt)).CurrentValue = createdAt;
    }

    private sealed record SeededRequests(
        Customer Ana,
        Customer Bruno,
        Customer Carla,
        Customer Diego,
        Guid NewestId,
        Guid MiddleId,
        Guid OldestId,
        Guid DiegoRequestId,
        string MiddleFileUrlPrefix);

    private sealed record JsonResponse(HttpStatusCode Status, JsonDocument Document) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    private sealed class ExplicitControllerFeatureProvider(params Type[] controllerTypes)
        : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
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
