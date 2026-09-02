using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Authentication;

public sealed class RolvixAuthorizationPolicyTests
{
    private const string AdminEmail = "admin@rolvix.test";

    [Fact]
    public async Task B2B_staff_is_allowed_by_default_and_denied_customer_and_platform_admin()
    {
        var harness = CreateHarness();
        var user = AuthenticatedPrincipal(
            new Claim("email", "staff@club.test"),
            new Claim(ClaimTypes.Name, "staff@club.test"));

        Assert.Equal(
            [SupabaseJwtBearerDefaults.AuthenticationScheme],
            harness.DefaultPolicy.AuthenticationSchemes);
        Assert.DoesNotContain(
            CustomerJwtBearerDefaults.AuthenticationScheme,
            harness.DefaultPolicy.AuthenticationSchemes);
        Assert.True((await harness.Authorization.AuthorizeAsync(user, resource: null, harness.DefaultPolicy)).Succeeded);
        Assert.False((await harness.Authorization.AuthorizeAsync(user, "Customer")).Succeeded);
        Assert.False((await harness.Authorization.AuthorizeAsync(user, SupabaseAuthenticationExtensions.PlatformAdminPolicy)).Succeeded);
    }

    [Fact]
    public async Task Customer_is_denied_default_and_platform_admin_and_allowed_customer_policy()
    {
        var harness = CreateHarness();
        var user = AuthenticatedPrincipal(
            new Claim(ClaimTypes.Role, AuthRoles.Customer),
            new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
            new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()),
            new Claim("email", "member@club.test"));

        Assert.False((await harness.Authorization.AuthorizeAsync(user, resource: null, harness.DefaultPolicy)).Succeeded);
        Assert.True((await harness.Authorization.AuthorizeAsync(user, "Customer")).Succeeded);
        Assert.False((await harness.Authorization.AuthorizeAsync(user, SupabaseAuthenticationExtensions.PlatformAdminPolicy)).Succeeded);
        Assert.False(
            (await harness.Authorization.AuthorizeAsync(
                user,
                PermissionPolicies.Name(Permissions.Core.DashboardRead))).Succeeded);
    }

    [Fact]
    public async Task Platform_admin_is_allowed_by_default_and_platform_admin_and_denied_customer()
    {
        var harness = CreateHarness();
        var user = AuthenticatedPrincipal(
            new Claim("email", AdminEmail),
            new Claim(ClaimTypes.Email, AdminEmail));

        Assert.True((await harness.Authorization.AuthorizeAsync(user, resource: null, harness.DefaultPolicy)).Succeeded);
        Assert.False((await harness.Authorization.AuthorizeAsync(user, "Customer")).Succeeded);
        Assert.True((await harness.Authorization.AuthorizeAsync(user, SupabaseAuthenticationExtensions.PlatformAdminPolicy)).Succeeded);
    }

    [Fact]
    public async Task Unauthenticated_principal_is_denied_all_rolvix_policies()
    {
        var harness = CreateHarness();
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False((await harness.Authorization.AuthorizeAsync(user, resource: null, harness.DefaultPolicy)).Succeeded);
        Assert.False((await harness.Authorization.AuthorizeAsync(user, "Customer")).Succeeded);
        Assert.False((await harness.Authorization.AuthorizeAsync(user, SupabaseAuthenticationExtensions.PlatformAdminPolicy)).Succeeded);
    }

    private static AuthzHarness CreateHarness()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<PlatformAdminOptions>(options => options.Emails = [AdminEmail]);
        services.AddSingleton<IPlatformAdminChecker, PlatformAdminChecker>();
        services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
        services.AddAuthorization(options => options.AddRolvixPolicies());
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();

        var provider = services.BuildServiceProvider();
        return new AuthzHarness(
            provider.GetRequiredService<IAuthorizationService>(),
            provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value.DefaultPolicy);
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test"));

    private sealed record AuthzHarness(
        IAuthorizationService Authorization,
        AuthorizationPolicy DefaultPolicy);
}
