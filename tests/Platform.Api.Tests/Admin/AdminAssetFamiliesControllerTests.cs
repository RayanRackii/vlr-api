using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
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
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Fakes;

namespace Platform.Api.Tests.Admin;

public sealed class AdminAssetFamiliesControllerTests
{
    private const string AdminEmail = "admin@rolvix.test";
    private const string StaffEmail = "staff@club.test";

    private static readonly Guid SpacesId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid InactiveId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MissingId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid ConflictId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task PlatformAdmin_without_tenant_returns_200_catalog()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var response = await client.GetAsync("/api/admin/asset-families");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var item = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(SpacesId, item.GetProperty("id").GetGuid());
        Assert.Equal("spaces", item.GetProperty("key").GetString());
        Assert.Equal("Spaces", item.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Array, item.GetProperty("fields").ValueKind);
        Assert.Equal(0, item.GetProperty("fields").GetArrayLength());
        Assert.Equal(1, item.GetProperty("sortOrder").GetInt32());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        Assert.False(HasProperty(item, "tenantId"));
        Assert.False(HasProperty(item, "TenantId"));
    }

    [Fact]
    public async Task Authenticated_tenant_admin_not_on_allowlist_returns_403()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, StaffEmail);

        var response = await client.GetAsync("/api/admin/asset-families");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Missing_auth_header_returns_401()
    {
        using var host = StartHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/api/admin/asset-families");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_includeInactive_returns_inactive_families()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var response = await client.GetAsync("/api/admin/asset-families?includeInactive=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.GetProperty("id").GetGuid() == InactiveId
            && item.GetProperty("isActive").GetBoolean() == false);
        Assert.All(items, item => Assert.False(HasProperty(item, "tenantId")));
    }

    [Fact]
    public async Task PlatformAdmin_get_by_id_returns_inactive_family()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var response = await client.GetAsync($"/api/admin/asset-families/{InactiveId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(InactiveId, doc.RootElement.GetProperty("id").GetGuid());
        Assert.False(doc.RootElement.GetProperty("isActive").GetBoolean());
        Assert.False(HasProperty(doc.RootElement, "tenantId"));
    }

    [Fact]
    public async Task PlatformAdmin_post_returns_201_without_tenantId()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var response = await client.PostAsJsonAsync(
            "/api/admin/asset-families",
            new
            {
                key = "courts",
                label = "Courts",
                fields = new[]
                {
                    new { key = "surface", type = "text", required = false, label = "Surface" },
                },
                sortOrder = 40,
                isActive = true,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("courts", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal("Courts", doc.RootElement.GetProperty("label").GetString());
        Assert.Equal(40, doc.RootElement.GetProperty("sortOrder").GetInt32());
        Assert.True(doc.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("fields").GetArrayLength());
        Assert.False(HasProperty(doc.RootElement, "tenantId"));
    }

    [Fact]
    public async Task PlatformAdmin_put_returns_200_without_tenantId()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var response = await client.PutAsJsonAsync(
            $"/api/admin/asset-families/{SpacesId}",
            new
            {
                label = "Quadras",
                fields = Array.Empty<object>(),
                sortOrder = 4,
                isActive = true,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(SpacesId, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("spaces", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal("Quadras", doc.RootElement.GetProperty("label").GetString());
        Assert.Equal(4, doc.RootElement.GetProperty("sortOrder").GetInt32());
        Assert.False(HasProperty(doc.RootElement, "tenantId"));
    }

    [Fact]
    public async Task PlatformAdmin_delete_returns_204()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var response = await client.DeleteAsync($"/api/admin/asset-families/{SpacesId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("post", "/api/admin/asset-families")]
    [InlineData("put", "/api/admin/asset-families/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("delete", "/api/admin/asset-families/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    public async Task Authenticated_tenant_admin_not_on_allowlist_cannot_mutate(string method, string path)
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, StaffEmail);

        var response = await client.SendAsync(Mutation(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("post", "/api/admin/asset-families")]
    [InlineData("put", "/api/admin/asset-families/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("delete", "/api/admin/asset-families/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    public async Task Missing_auth_header_cannot_mutate(string method, string path)
    {
        using var host = StartHost();
        var client = host.GetTestClient();

        var response = await client.SendAsync(Mutation(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Validation_and_conflicts_map_to_400_404_and_409()
    {
        using var host = StartHost();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, AdminEmail);

        var invalid = await client.PostAsJsonAsync(
            "/api/admin/asset-families",
            new { key = "bad key", label = "Bad", fields = Array.Empty<object>(), sortOrder = 1, isActive = true });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ReadErrorAsync(invalid)));

        var missing = await client.GetAsync($"/api/admin/asset-families/{MissingId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ReadErrorAsync(missing)));

        var conflict = await client.DeleteAsync($"/api/admin/asset-families/{ConflictId}");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await ReadErrorAsync(conflict)));
    }

    private static HttpRequestMessage Mutation(string method, string path)
    {
        var httpMethod = method switch
        {
            "post" => HttpMethod.Post,
            "put" => HttpMethod.Put,
            "delete" => HttpMethod.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported method."),
        };

        var request = new HttpRequestMessage(httpMethod, path);
        if (httpMethod == HttpMethod.Post || httpMethod == HttpMethod.Put)
        {
            request.Content = JsonContent.Create(new
            {
                key = "courts",
                label = "Courts",
                fields = Array.Empty<object>(),
                sortOrder = 1,
                isActive = true,
            });
        }

        return request;
    }

    private static IHost StartHost() =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IAssetFamilyService, FakeCatalogAssetFamilyService>();
                    services.AddSingleton<IPlatformAdminChecker>(_ => new FakePlatformAdminChecker(AdminEmail));
                    services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                    services.AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
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
                                new AssemblyPart(typeof(AdminAssetFamiliesController).Assembly));
                            manager.FeatureProviders.Add(
                                new SingleControllerFeatureProvider(typeof(AdminAssetFamiliesController)));
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

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static bool HasProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FakeCatalogAssetFamilyService : IAssetFamilyService
    {
        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetFamilyDetailResponse>>(
            [
                new AssetFamilyDetailResponse(
                    SpacesId,
                    "spaces",
                    "Spaces",
                    [],
                    SortOrder: 1,
                    IsActive: true),
            ]);

        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveForTenantAsync(
            Guid tenantId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogIncludingInactiveAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AssetFamilyDetailResponse>>(
            [
                ActiveFamily(),
                InactiveFamily(),
            ]);

        public Task<AssetFamilyDetailResponse> GetByIdAsync(
            Guid id,
            CancellationToken cancellationToken)
        {
            if (id == MissingId)
            {
                throw new KeyNotFoundException("Asset family was not found.");
            }

            if (id == InactiveId)
            {
                return Task.FromResult(InactiveFamily());
            }

            return Task.FromResult(ActiveFamily());
        }

        public Task<AssetFamilyDetailResponse> CreateAsync(
            CreateAssetFamilyRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Key.Contains(' ', StringComparison.Ordinal))
            {
                throw new ArgumentException("Key is invalid.");
            }

            return Task.FromResult(new AssetFamilyDetailResponse(
                SpacesId,
                request.Key,
                request.Label,
                request.Fields,
                request.SortOrder,
                request.IsActive));
        }

        public Task<AssetFamilyDetailResponse> UpdateAsync(
            Guid id,
            UpdateAssetFamilyRequest request,
            CancellationToken cancellationToken)
        {
            if (id == MissingId)
            {
                throw new KeyNotFoundException("Asset family was not found.");
            }

            return Task.FromResult(new AssetFamilyDetailResponse(
                id,
                "spaces",
                request.Label,
                request.Fields,
                request.SortOrder,
                request.IsActive));
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            if (id == MissingId)
            {
                throw new KeyNotFoundException("Asset family was not found.");
            }

            if (id == ConflictId)
            {
                throw new InvalidOperationException("Asset family is in use and cannot be deleted.");
            }

            return Task.CompletedTask;
        }

        private static AssetFamilyDetailResponse ActiveFamily() =>
            new(
                SpacesId,
                "spaces",
                "Spaces",
                [],
                SortOrder: 1,
                IsActive: true);

        private static AssetFamilyDetailResponse InactiveFamily() =>
            new(
                InactiveId,
                "legacy",
                "Legacy",
                [],
                SortOrder: 9,
                IsActive: false);
    }

    private sealed class SingleControllerFeatureProvider(Type controllerType)
        : ControllerFeatureProvider
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
            ];

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
