using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Tests.Fakes;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Authentication;

public sealed class HttpContextTenantProviderTests
{
    private const string AdminEmail = "admin@rolvix.test";

    [Fact]
    public void Authenticated_customer_identity_is_used_when_unauthenticated_identity_is_first()
    {
        var tenantId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var unauthenticated = new ClaimsIdentity();
        var customer = new ClaimsIdentity(
            [
                new Claim(CustomerClaimTypes.CustomerId, customerId.ToString()),
                new Claim(CustomerClaimTypes.TenantId, tenantId.ToString()),
                new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
                new Claim(ClaimTypes.Role, AuthRoles.Customer),
            ],
            authenticationType: CustomerJwtBearerDefaults.AuthenticationScheme);

        var provider = CreateProvider(new ClaimsPrincipal([unauthenticated, customer]));

        Assert.Equal(tenantId, provider.TenantId);
    }

    [Fact]
    public void Authenticated_customer_missing_tenant_id_throws()
    {
        var customer = new ClaimsIdentity(
            [
                new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()),
                new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
                new Claim(ClaimTypes.Role, AuthRoles.Customer),
            ],
            authenticationType: CustomerJwtBearerDefaults.AuthenticationScheme);

        var provider = CreateProvider(new ClaimsPrincipal(customer));

        var exception = Assert.Throws<TenantResolutionException>(() => provider.TenantId);
        Assert.Equal("The customer access token is missing a valid tenant_id claim.", exception.Message);
    }

    [Fact]
    public void Authenticated_customer_with_platform_admin_email_resolves_customer_tenant()
    {
        var tenantId = Guid.NewGuid();
        var customer = new ClaimsIdentity(
            [
                new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()),
                new Claim(CustomerClaimTypes.TenantId, tenantId.ToString()),
                new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
                new Claim(ClaimTypes.Role, AuthRoles.Customer),
                new Claim("email", AdminEmail),
            ],
            authenticationType: CustomerJwtBearerDefaults.AuthenticationScheme);

        var provider = CreateProvider(new ClaimsPrincipal(customer), AdminEmail);

        Assert.Equal(tenantId, provider.TenantId);
    }

    [Fact]
    public void Authenticated_customer_with_platform_admin_email_missing_tenant_id_throws()
    {
        var customer = new ClaimsIdentity(
            [
                new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()),
                new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
                new Claim(ClaimTypes.Role, AuthRoles.Customer),
                new Claim("email", AdminEmail),
            ],
            authenticationType: CustomerJwtBearerDefaults.AuthenticationScheme);

        var provider = CreateProvider(new ClaimsPrincipal(customer), AdminEmail);

        var exception = Assert.Throws<TenantResolutionException>(() => provider.TenantId);
        Assert.Equal("The customer access token is missing a valid tenant_id claim.", exception.Message);
    }

    [Fact]
    public void Unauthenticated_only_returns_null()
    {
        var unauthenticated = new ClaimsIdentity(
        [
            new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()),
            new Claim(CustomerClaimTypes.TenantId, Guid.NewGuid().ToString()),
        ]);

        var provider = CreateProvider(new ClaimsPrincipal(unauthenticated));

        Assert.Null(provider.TenantId);
    }

    private static HttpContextTenantProvider CreateProvider(
        ClaimsPrincipal user,
        params string[] platformAdminEmails)
    {
        var accessor = new FakeHttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = user },
        };

        return new HttpContextTenantProvider(
            accessor,
            new AmbientTenantContext(),
            new PlatformAdminChecker(Options.Create(new PlatformAdminOptions
            {
                Emails = [.. platformAdminEmails],
            })));
    }
}
