using Microsoft.AspNetCore.Authorization;

namespace Platform.Api.Authentication;

public static class RolvixAuthorizationOptions
{
    public static AuthorizationOptions AddRolvixPolicies(this AuthorizationOptions options)
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
                .AddAuthenticationSchemes(CustomerJwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireRole(AuthRoles.Customer));

        options.AddPolicy(
            SupabaseAuthenticationExtensions.PlatformAdminPolicy,
            policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformAdminRequirement()));

        return options;
    }
}
