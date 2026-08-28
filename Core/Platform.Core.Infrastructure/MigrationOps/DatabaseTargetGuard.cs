using System.Text.RegularExpressions;
using Npgsql;

namespace Platform.Core.Infrastructure.MigrationOps;

public static class DatabaseTargetGuard
{
    private static readonly Regex ProjectRefPattern = new(
        "^[a-z0-9]{10,32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static DatabaseTargetIdentity Validate(string target, string? connectionString)
    {
        var normalizedTarget = DatabaseTargets.NormalizeTarget(target);
        var expectedRef = DatabaseTargets.ExpectedRef(normalizedTarget);
        var detectedRef = ExtractSupabaseProjectRef(connectionString);

        var identity = new DatabaseTargetIdentity(normalizedTarget, expectedRef, detectedRef);
        if (!identity.IsSafe)
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.Mismatch,
                $"{DatabaseTargetCodes.Mismatch}: target={normalizedTarget} expected={expectedRef} detected={detectedRef}.");
        }

        return identity;
    }

    public static string ExtractSupabaseProjectRef(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.ConnectionMissing,
                "ConnectionStrings__DefaultConnection is missing.");
        }

        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is not DatabaseTargetException)
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.ConnectionMalformed,
                $"Connection string is malformed ({ex.GetType().Name}).");
        }

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            throw new DatabaseTargetException(
                DatabaseTargetCodes.ConnectionMalformed,
                "Connection string is missing Host.");
        }

        var fromUsername = ExtractRefFromUsername(builder.Username);
        if (fromUsername is not null)
        {
            return fromUsername;
        }

        var fromHost = ExtractRefFromHost(builder.Host);
        if (fromHost is not null)
        {
            return fromHost;
        }

        throw new DatabaseTargetException(
            DatabaseTargetCodes.ConnectionMalformed,
            "Could not extract a Supabase project ref from Username or Host.");
    }

    private static string? ExtractRefFromUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        const string prefix = "postgres.";
        if (!username.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = username[prefix.Length..].Trim().ToLowerInvariant();
        return ProjectRefPattern.IsMatch(candidate) ? candidate : null;
    }

    private static string? ExtractRefFromHost(string host)
    {
        const string suffix = ".supabase.co";
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length < 3 || !labels[0].Equals("db", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var candidate = labels[1].ToLowerInvariant();
        return ProjectRefPattern.IsMatch(candidate) ? candidate : null;
    }
}
