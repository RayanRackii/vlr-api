using Microsoft.AspNetCore.Authorization;

namespace Platform.Api.Authorization;

/// <summary>
/// B2B authorization succeeds when the current tenant user has any one of the listed permissions.
/// </summary>
public sealed class RequireAnyPermissionAttribute : AuthorizeAttribute
{
    public RequireAnyPermissionAttribute(params string[] permissionKeys)
    {
        if (permissionKeys.Length == 0)
        {
            throw new ArgumentException("At least one permission key is required.", nameof(permissionKeys));
        }

        Policy = PermissionPolicies.AnyName(permissionKeys);
    }
}
