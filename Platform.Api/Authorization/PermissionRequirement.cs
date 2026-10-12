using Microsoft.AspNetCore.Authorization;

namespace Platform.Api.Authorization;

public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permissionKey)
        : this([permissionKey])
    {
    }

    public PermissionRequirement(IEnumerable<string> permissionKeys)
    {
        PermissionKeys = permissionKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (PermissionKeys.Count == 0)
        {
            throw new ArgumentException("At least one permission key is required.", nameof(permissionKeys));
        }
    }

    public IReadOnlyList<string> PermissionKeys { get; }

    public string PermissionKey => PermissionKeys[0];
}
