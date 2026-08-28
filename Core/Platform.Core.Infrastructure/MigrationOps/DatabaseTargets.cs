namespace Platform.Core.Infrastructure.MigrationOps;

public static class DatabaseTargets
{
    public const string Development = "development";

    public const string Production = "production";

    public const string DevelopmentSupabaseRef = "jzptnjyzijklutinpxag";

    public const string ProductionSupabaseRef = "kbptdzfbngelzdhriyhf";

    public static string ExpectedRef(string target) =>
        NormalizeTarget(target) switch
        {
            Development => DevelopmentSupabaseRef,
            Production => ProductionSupabaseRef,
            _ => throw new DatabaseTargetException(
                DatabaseTargetCodes.TargetInvalid,
                $"Unknown database target '{target}'. Use '{Development}' or '{Production}'.")
        };

    public static string NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.TargetInvalid,
                "Database target is required. Use 'development' or 'production'.");
        }

        var normalized = target.Trim().ToLowerInvariant();
        if (normalized is not (Development or Production))
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.TargetInvalid,
                $"Unknown database target '{target}'. Use '{Development}' or '{Production}'.");
        }

        return normalized;
    }

    public static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.ModeInvalid,
                "Migration mode is required. Use 'list' or 'apply'.");
        }

        var normalized = mode.Trim().ToLowerInvariant();
        if (normalized is not ("list" or "apply"))
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.ModeInvalid,
                $"Unknown migration mode '{mode}'. Use 'list' or 'apply'.");
        }

        return normalized;
    }
}
