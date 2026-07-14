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

            if (httpContext is null || httpContext.User.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var user = httpContext.User;

            // Platform Super-Admins operate across all tenants (disable EF tenant filter).
            if (_platformAdminChecker.IsPlatformAdmin(user))
            {
                return null;
            }

            if (user.IsInRole(AuthRoles.Customer)
                || user.FindFirst(CustomerClaimTypes.CustomerId) is not null)
            {
                var tenantClaim = user.FindFirst(CustomerClaimTypes.TenantId)?.Value;

                if (Guid.TryParse(tenantClaim, out var customerTenantId))
                {
                    return customerTenantId;
                }

                throw new TenantResolutionException(
                    "The customer access token is missing a valid tenant_id claim.");
            }

            return SupabaseAppMetadataParser.ExtractTenantId(user);
        }
    }
}
