using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;

namespace Platform.Api.Authentication;

/// <summary>
/// Unvalidated JWT issuer peek used only to forward the default Bearer PolicyScheme.
/// Does not validate signatures or trust claims for tenant/authz.
/// </summary>
public static class JwtBearerSchemeSelector
{
    public static string Select(HttpContext context)
    {
        try
        {
            var headerValue = context.Request.Headers.Authorization.ToString();
            if (!AuthenticationHeaderValue.TryParse(headerValue, out var header)
                || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(header.Parameter))
            {
                return SupabaseJwtBearerDefaults.AuthenticationScheme;
            }

            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(header.Parameter))
            {
                return SupabaseJwtBearerDefaults.AuthenticationScheme;
            }

            var token = handler.ReadJwtToken(header.Parameter);
            if (string.Equals(token.Issuer, CustomerJwtIssuer.Issuer, StringComparison.Ordinal))
            {
                return CustomerJwtBearerDefaults.AuthenticationScheme;
            }
        }
        catch
        {
            // Malformed tokens must not throw; the forwarded handler returns 401.
        }

        return SupabaseJwtBearerDefaults.AuthenticationScheme;
    }
}
