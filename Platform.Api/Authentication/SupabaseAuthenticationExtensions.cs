using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Platform.Api.Authentication;

public static class SupabaseAuthenticationExtensions
{
    public static IServiceCollection AddSupabaseAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var supabaseUrl = configuration["Supabase:Url"]
            ?? throw new InvalidOperationException("Supabase:Url is not configured.");

        var jwtSecret = configuration["Supabase:JwtSecret"]
            ?? throw new InvalidOperationException("Supabase:JwtSecret is not configured.");

        var issuer = $"{supabaseUrl.TrimEnd('/')}/auth/v1";

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Current Supabase projects sign access tokens with asymmetric keys (ES256/RS256)
                // exposed at the JWKS discovery document — not only with the legacy HS256 secret.
                options.MetadataAddress = $"{issuer}/.well-known/openid-configuration";
                options.MapInboundClaims = false;
                options.RefreshOnIssuerKeyNotFound = true;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    // Legacy HS256 fallback when tokens are still signed with the JWT secret.
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    ValidAlgorithms =
                    [
                        SecurityAlgorithms.EcdsaSha256,
                        SecurityAlgorithms.RsaSha256,
                        SecurityAlgorithms.HmacSha256,
                    ],
                };
            });

        services.AddAuthorization();

        return services;
    }
}
