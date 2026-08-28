using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Core.Infrastructure.MigrationOps;

public static class MigrationDbContextFactory
{
    public const string HistoryTable = "__ef_migrations_history";

    public const string HistorySchema = "core";

    public static AppDbContext Create(string connectionString)
    {
        var bounded = NpgsqlConnectionStringHelper.WithBoundedPoolSize(connectionString, maxPoolSize: 2);
        var dataSource = new NpgsqlDataSourceBuilder(bounded)
            .EnableDynamicJson()
            .Build();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                dataSource,
                npgsql => npgsql.MigrationsHistoryTable(HistoryTable, HistorySchema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new NullTenantProvider());
    }
}
