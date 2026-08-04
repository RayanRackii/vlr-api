using Npgsql;

namespace Platform.Core.Infrastructure.Persistence;

public static class NpgsqlConnectionStringHelper
{
    /// <summary>
    /// Caps client pool size for Supabase session pooler (often ~15 slots shared
    /// across the project). Hangfire + EF each open their own pool.
    /// </summary>
    public static string WithBoundedPoolSize(string connectionString, int maxPoolSize)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        if (builder.MaxPoolSize <= 0 || builder.MaxPoolSize > maxPoolSize)
        {
            builder.MaxPoolSize = maxPoolSize;
        }

        // Avoid holding idle sessions forever on tiny Supabase pools.
        if (builder.ConnectionIdleLifetime <= 0 || builder.ConnectionIdleLifetime > 60)
        {
            builder.ConnectionIdleLifetime = 30;
        }

        return builder.ConnectionString;
    }
}
