namespace Platform.Api.Authorization;

public static class PermissionPolicies
{
    public const string Prefix = "perm:";

    public static string Name(string permissionKey) => Prefix + permissionKey;

    public static bool TryParse(string policyName, out string permissionKey)
    {
        if (policyName.StartsWith(Prefix, StringComparison.Ordinal)
            && policyName.Length > Prefix.Length)
        {
            permissionKey = policyName[Prefix.Length..];
            return true;
        }

        permissionKey = string.Empty;
        return false;
    }
}
