using System.Security.Claims;

namespace Platform.Api.Authentication;

public static class AuthRoles
{
    public const string Customer = "Customer";
}

public static class CustomerClaimTypes
{
    public const string CustomerId = "customer_id";

    public const string TenantId = "tenant_id";

    public const string Role = "role";

    public static bool IsCustomer(ClaimsPrincipal? user) =>
        user is not null
        && (user.IsInRole(AuthRoles.Customer) || user.FindFirst(CustomerId) is not null);
}
