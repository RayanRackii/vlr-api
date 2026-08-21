using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Production-shaped <see cref="AppDbContext"/> against PostgreSQL (Npgsql + snake_case).
/// Not <see cref="TestAppDbContext"/> — jsonb and migrations must match the host.
/// </summary>
public sealed class PostgresAppDbFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly DbContextOptions<AppDbContext> _options;

    public PostgresAppDbFactory(string connectionString)
    {
        _dataSource = new NpgsqlDataSourceBuilder(connectionString)
            .EnableDynamicJson()
            .Build();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                _dataSource,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "core"))
            .UseSnakeCaseNamingConvention()
            .Options;
    }

    public AppDbContext Create(ITenantProvider tenantProvider) =>
        new(_options, tenantProvider);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
