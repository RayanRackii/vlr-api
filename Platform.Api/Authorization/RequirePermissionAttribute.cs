using Microsoft.AspNetCore.Authorization;

namespace Platform.Api.Authorization;

/// <summary>
/// B2B permission check. Policy name is <c>perm:{key}</c> and includes the default B2B policy.
/// </summary>
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permissionKey)
    {
        Policy = PermissionPolicies.Name(permissionKey);
    }
}
