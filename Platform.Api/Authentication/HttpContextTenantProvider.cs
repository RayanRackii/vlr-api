using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authentication;

public sealed class HttpContextTenantProvider : ITenantProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTenantProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? TenantId
    {
        get
        {
            var httpContext = _httpContextAccessor.HttpContext;

            if (httpContext is null || httpContext.User.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            return SupabaseAppMetadataParser.ExtractTenantId(httpContext.User);
        }
    }
}
