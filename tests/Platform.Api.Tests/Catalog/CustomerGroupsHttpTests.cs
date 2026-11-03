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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Catalog;

public sealed class CustomerGroupsHttpTests
{
    [Fact]
    public async Task Read_only_staff_can_read_but_not_change_access_or_groups()
    {
        using var host = StartHost(Permissions.Catalog.ProductsRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/catalog/products/{id}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/customer-groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/catalog/products/{id}/access",
            new UpdateCatalogProductAccessRequest("Restricted", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/catalog/customer-groups",
            new UpsertCustomerGroupRequest("VIP", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/catalog/customer-groups/{id}")).StatusCode);
    }

    [Fact]
    public async Task Staff_without_products_read_cannot_list_groups()
    {
        using var host = StartHost(Permissions.Catalog.OrdersRead);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.HeaderName, "ops@club.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/catalog/customer-groups")).StatusCode);
    }

    private static IHost StartHost(params string[] allowedPermissionKeys) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<ICustomerGroupService, StubGroupService>();
                    services.AddSingleton<ITenantModuleAccessor, CatalogOnlyModules>();
                    services.AddSingleton<ITenantProvider, StubTenantProvider>();
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

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(CustomerGroupsController).Assembly));
                            manager.FeatureProviders.Add(new SingleControllerFeatureProvider(typeof(CustomerGroupsController)));
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
            if (allowedKeys.Contains(requirement.PermissionKey))
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubGroupService : ICustomerGroupService
    {
        public Task<IReadOnlyList<CustomerGroupResponse>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<CustomerGroupResponse>>([]);
        public Task<CustomerGroupResponse?> GetAsync(Guid id, CancellationToken ct) => Task.FromResult<CustomerGroupResponse?>(null);
        public Task<CustomerGroupResponse> CreateAsync(UpsertCustomerGroupRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<CustomerGroupResponse?> UpdateAsync(Guid id, UpsertCustomerGroupRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<CustomerGroupResponse?> AddMembersAsync(Guid id, CustomerGroupMembersRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<CustomerGroupResponse?> RemoveMemberAsync(Guid id, Guid customerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CatalogProductAccessResponse?> GetProductAccessAsync(Guid productId, CancellationToken ct) =>
            Task.FromResult<CatalogProductAccessResponse?>(new CatalogProductAccessResponse(productId, "Public", [], []));
        public Task<CatalogProductAccessResponse?> SetProductAccessAsync(Guid productId, UpdateCatalogProductAccessRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogAccessAuditResponse>> ListHistoryAsync(string entityType, Guid entityId, CancellationToken ct) => Task.FromResult<IReadOnlyList<CatalogAccessAuditResponse>>([]);
    }

    private sealed class CatalogOnlyModules : ITenantModuleAccessor
    {
        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string> { PlatformModules.Catalog });
    }

    private sealed class StubTenantProvider : ITenantProvider
    {
        public Guid? TenantId => Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class SingleControllerFeatureProvider(params Type[] controllerTypes)
        : ControllerFeatureProvider
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
