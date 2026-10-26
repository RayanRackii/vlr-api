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

    [Fact]
    public void Platform_admin_ignores_access_token_tenant_until_session_is_resolved()
    {
        var staleTenantId = Guid.NewGuid();
        var identity = StaffIdentity(staleTenantId);
        var provider = CreateProvider(
            new ClaimsPrincipal(identity),
            new PlatformAdminTenantSession(),
            AdminEmail);

        Assert.Null(provider.TenantId);
    }

    [Fact]
    public void Platform_admin_uses_resolved_session_instead_of_stale_access_token()
    {
        var staleTenantId = Guid.NewGuid();
        var liveTenantId = Guid.NewGuid();
        var session = new PlatformAdminTenantSession();
        session.Resolve(liveTenantId);
        var provider = CreateProvider(
            new ClaimsPrincipal(StaffIdentity(staleTenantId)),
            session,
            AdminEmail);

        Assert.Equal(liveTenantId, provider.TenantId);
    }

    [Fact]
    public void Platform_admin_resolved_without_tenant_ignores_stale_access_token()
    {
        var session = new PlatformAdminTenantSession();
        session.Resolve(null);
        var provider = CreateProvider(
            new ClaimsPrincipal(StaffIdentity(Guid.NewGuid())),
            session,
            AdminEmail);

        Assert.Null(provider.TenantId);
    }

    [Fact]
    public void Ambient_tenant_wins_over_a_resolved_platform_admin_session()
    {
        var ambientTenantId = Guid.NewGuid();
        var session = new PlatformAdminTenantSession();
        session.Resolve(Guid.NewGuid());
        var accessor = new FakeHttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(StaffIdentity(Guid.NewGuid())),
            },
        };
        var provider = new HttpContextTenantProvider(
            accessor,
            new AmbientTenantContext { TenantId = ambientTenantId },
            new PlatformAdminChecker(Options.Create(new PlatformAdminOptions
            {
                Emails = [AdminEmail],
            })),
            session);

        Assert.Equal(ambientTenantId, provider.TenantId);
    }

    [Fact]
    public void Staff_still_resolves_tenant_from_app_metadata()
    {
        var tenantId = Guid.NewGuid();
        var provider = CreateProvider(new ClaimsPrincipal(StaffIdentity(tenantId)));

        Assert.Equal(tenantId, provider.TenantId);
    }

    [Fact]
    public void Staff_missing_app_metadata_tenant_throws()
    {
        var identity = new ClaimsIdentity(
            [new Claim("email", "staff@rolvix.test")],
            authenticationType: "Bearer");
        var provider = CreateProvider(new ClaimsPrincipal(identity));

        var exception = Assert.Throws<TenantResolutionException>(() => provider.TenantId);
        Assert.Equal(
            "The access token is missing a valid app_metadata.tenant_id.",
            exception.Message);
    }

    [Fact]
    public void Customer_with_ambient_tenant_of_another_tenant_throws_without_naming_it()
    {
        var claimTenantId = Guid.NewGuid();
        var ambientTenantId = Guid.NewGuid();
        var provider = CreateProvider(
            new ClaimsPrincipal(CustomerIdentity(claimTenantId)),
            new AmbientTenantContext { TenantId = ambientTenantId });

        var exception = Assert.Throws<TenantResolutionException>(() => provider.TenantId);
        Assert.DoesNotContain(ambientTenantId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(claimTenantId.ToString(), exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Customer_with_ambient_tenant_equal_to_claim_resolves()
    {
        var tenantId = Guid.NewGuid();
        var provider = CreateProvider(
            new ClaimsPrincipal(CustomerIdentity(tenantId)),
            new AmbientTenantContext { TenantId = tenantId });

        Assert.Equal(tenantId, provider.TenantId);
    }

    // Passo 6 follow-up (Sol): before the guard, a bad claim fell back to the ambient tenant.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public void Customer_with_missing_or_invalid_tenant_claim_and_ambient_tenant_throws(string? tenantClaim)
    {
        var claims = new List<Claim>
        {
            new(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()),
            new(CustomerClaimTypes.Role, AuthRoles.Customer),
            new(ClaimTypes.Role, AuthRoles.Customer),
        };
        if (tenantClaim is not null)
        {
            claims.Add(new Claim(CustomerClaimTypes.TenantId, tenantClaim));
        }

        var provider = CreateProvider(
            new ClaimsPrincipal(new ClaimsIdentity(claims, CustomerJwtBearerDefaults.AuthenticationScheme)),
            new AmbientTenantContext { TenantId = Guid.NewGuid() });

        var exception = Assert.Throws<TenantResolutionException>(() => provider.TenantId);
        Assert.Equal("The customer access token is missing a valid tenant_id claim.", exception.Message);
    }

    [Fact]
    public void Staff_ambient_tenant_still_wins_over_app_metadata()
    {
        var ambientTenantId = Guid.NewGuid();
        var provider = CreateProvider(
            new ClaimsPrincipal(StaffIdentity(Guid.NewGuid())),
            new AmbientTenantContext { TenantId = ambientTenantId });

        Assert.Equal(ambientTenantId, provider.TenantId);
    }

    [Fact]
    public void Ambient_tenant_without_http_context_is_used()
    {
        var ambientTenantId = Guid.NewGuid();
        var provider = new HttpContextTenantProvider(
            new FakeHttpContextAccessor(),
            new AmbientTenantContext { TenantId = ambientTenantId },
            new PlatformAdminChecker(Options.Create(new PlatformAdminOptions())),
            new PlatformAdminTenantSession());

        Assert.Equal(ambientTenantId, provider.TenantId);
    }

    private static ClaimsIdentity CustomerIdentity(Guid tenantId) =>
        new(
            [
                new Claim(CustomerClaimTypes.CustomerId, Guid.NewGuid().ToString()),
                new Claim(CustomerClaimTypes.TenantId, tenantId.ToString()),
                new Claim(CustomerClaimTypes.Role, AuthRoles.Customer),
                new Claim(ClaimTypes.Role, AuthRoles.Customer),
            ],
            authenticationType: CustomerJwtBearerDefaults.AuthenticationScheme);

    private static HttpContextTenantProvider CreateProvider(
        ClaimsPrincipal user,
        AmbientTenantContext ambient) =>
        new(
            new FakeHttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } },
            ambient,
            new PlatformAdminChecker(Options.Create(new PlatformAdminOptions())),
            new PlatformAdminTenantSession());

    private static ClaimsIdentity StaffIdentity(Guid tenantId) =>
        new(
            [
                new Claim("email", AdminEmail),
                new Claim(
                    TenantClaimTypes.AppMetadata,
                    $"{{\"tenant_id\":\"{tenantId}\"}}"),
            ],
            authenticationType: "Bearer");

    private static HttpContextTenantProvider CreateProvider(
        ClaimsPrincipal user,
        params string[] platformAdminEmails) =>
        CreateProvider(user, new PlatformAdminTenantSession(), platformAdminEmails);

    private static HttpContextTenantProvider CreateProvider(
        ClaimsPrincipal user,
        PlatformAdminTenantSession session,
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
            })),
            session);
    }
}
