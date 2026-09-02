using System.Security.Claims;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authentication;

public sealed class HttpContextTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AmbientTenantContext _ambientTenantContext;
    private readonly IPlatformAdminChecker _platformAdminChecker;

    public HttpContextTenantProvider(
        IHttpContextAccessor httpContextAccessor,
        AmbientTenantContext ambientTenantContext,
        IPlatformAdminChecker platformAdminChecker)
    {
        _httpContextAccessor = httpContextAccessor;
        _ambientTenantContext = ambientTenantContext;
        _platformAdminChecker = platformAdminChecker;
    }

    public Guid? TenantId
    {
        get
        {
            if (_ambientTenantContext.TenantId is Guid ambientTenantId)
            {
                return ambientTenantId;
            }

            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is null)
            {
                return null;
            }

            var authenticatedIdentities = httpContext.User.Identities
                .Where(identity => identity.IsAuthenticated)
                .ToArray();

            if (authenticatedIdentities.Length == 0)
            {
                return null;
            }

            var user = new ClaimsPrincipal(authenticatedIdentities);

            // Customer tokens always resolve tenant_id (or throw). An allowlisted
            // platform-admin email on a B2C JWT must never fall through to
            // cross-tenant platform mode (null tenant → GQF off).
            if (CustomerClaimTypes.IsCustomer(user))
            {
                var tenantClaim = user.FindFirst(CustomerClaimTypes.TenantId)?.Value;

                if (Guid.TryParse(tenantClaim, out var customerTenantId))
                {
                    return customerTenantId;
                }

                throw new TenantResolutionException(
                    "The customer access token is missing a valid tenant_id claim.");
            }

            // Platform Super-Admins: no tenant_id → cross-tenant platform mode.
            // With tenant_id in JWT → operating inside that tenant as admin.
            if (_platformAdminChecker.IsPlatformAdmin(user))
            {
                return SupabaseAppMetadataParser.TryExtractTenantId(user);
            }

            return SupabaseAppMetadataParser.ExtractTenantId(user);
        }
    }
}
