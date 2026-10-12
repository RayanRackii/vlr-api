namespace Platform.Api.Authorization;

public static class PermissionPolicies
{
    public const string Prefix = "perm:";
    public const string AnyPrefix = "perm-any:";

    public static string Name(string permissionKey) => Prefix + permissionKey;

    public static string AnyName(params string[] permissionKeys) =>
        AnyPrefix + string.Join('|', permissionKeys);

    public static bool TryParseAny(string policyName, out IReadOnlyList<string> permissionKeys)
    {
        if (policyName.StartsWith(AnyPrefix, StringComparison.Ordinal)
            && policyName.Length > AnyPrefix.Length)
        {
            permissionKeys = policyName[AnyPrefix.Length..]
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return permissionKeys.Count > 0;
        }

        permissionKeys = [];
        return false;
    }

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
