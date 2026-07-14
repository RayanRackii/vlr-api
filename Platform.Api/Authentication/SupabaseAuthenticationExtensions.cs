using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Platform.Api.Authentication;

public static class SupabaseAuthenticationExtensions
{
    public const string PlatformAdminPolicy = "PlatformAdmin";

    public static IServiceCollection AddSupabaseAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var supabaseUrl = configuration["Supabase:Url"]
            ?? throw new InvalidOperationException("Supabase:Url is not configured.");

        var jwtSecret = configuration["Supabase:JwtSecret"]
            ?? throw new InvalidOperationException("Supabase:JwtSecret is not configured.");

        var issuer = $"{supabaseUrl.TrimEnd('/')}/auth/v1";

        services.Configure<PlatformAdminOptions>(
            configuration.GetSection(PlatformAdminOptions.SectionName));
        services.AddSingleton<IPlatformAdminChecker, PlatformAdminChecker>();
        services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();

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
                    // Also used to validate platform-issued B2C Customer OTPs.
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    RoleClaimType = CustomerClaimTypes.Role,
                    ValidAlgorithms =
                    [
                        SecurityAlgorithms.EcdsaSha256,
                        SecurityAlgorithms.RsaSha256,
                        SecurityAlgorithms.HmacSha256,
                    ],
                };
            });

        services.AddAuthorization(options =>
        {
            // Plain [Authorize] protects B2B panel routes: Customer B2C JWTs are rejected.
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireAssertion(context =>
                    !context.User.IsInRole(AuthRoles.Customer)
                    && context.User.FindFirst(CustomerClaimTypes.CustomerId) is null)
                .Build();

            options.AddPolicy(
                "Customer",
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireRole(AuthRoles.Customer));

            options.AddPolicy(
                PlatformAdminPolicy,
                policy => policy
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PlatformAdminRequirement()));
        });

        services.AddScoped<IPublicTenantBinder, PublicTenantBinder>();
        services.AddSingleton<ICustomerJwtIssuer, CustomerJwtIssuer>();

        return services;
    }
}
