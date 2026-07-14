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
}
