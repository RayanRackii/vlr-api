using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Authorization;

public sealed class RequireActiveModuleNullTenantTests
{
    [Fact]
    public async Task Null_tenant_is_denied_with_module_403()
    {
        var context = CreateContext([]);

        await new RequireActiveModuleAttribute(PlatformModules.Rentals).OnAuthorizationAsync(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleError, body.RootElement.GetProperty("error").GetString());
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(PlatformModules.Rentals, body.RootElement.GetProperty("module").GetString());
    }

    [Fact]
    public async Task Null_tenant_on_platform_admin_only_action_passes()
    {
        var context = CreateContext(
            [new AuthorizeAttribute(SupabaseAuthenticationExtensions.PlatformAdminPolicy)]);

        await new RequireActiveModuleAttribute(PlatformModules.Rentals).OnAuthorizationAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Null_tenant_inside_platform_scope_passes()
    {
        var context = CreateContext([]);

        using (PlatformScope.Begin(NullLogger.Instance, "test"))
        {
            await new RequireActiveModuleAttribute(PlatformModules.Rentals).OnAuthorizationAsync(context);
        }

        Assert.Null(context.Result);
    }

    private static AuthorizationFilterContext CreateContext(IList<object> endpointMetadata)
    {
        var services = new ServiceCollection()
            .AddSingleton<ITenantProvider>(new FakeTenantProvider { TenantId = null })
            .AddSingleton<ITenantModuleAccessor, ThrowingModuleAccessor>()
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ActionDescriptor { EndpointMetadata = endpointMetadata });
        return new AuthorizationFilterContext(actionContext, []);
    }

    private sealed class ThrowingModuleAccessor : ITenantModuleAccessor
    {
        public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Module keys must not be read without a tenant.");
    }
}
