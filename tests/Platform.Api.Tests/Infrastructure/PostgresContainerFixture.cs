using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Tests.Fakes;
using Testcontainers.PostgreSql;

namespace Platform.Api.Tests.Infrastructure;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _localAdminConnectionString;
    private string? _localDatabaseName;

    public PostgresAppDbFactory? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        if (LocalPostgresEnvironment.IsConfigured)
        {
            await InitializeLocalPostgresAsync();
            return;
        }

        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

        await _container.StartAsync();
        Factory = new PostgresAppDbFactory(_container.GetConnectionString());

        await using var db = Factory.Create(new FakeTenantProvider());
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (_localAdminConnectionString is not null && _localDatabaseName is not null)
        {
            await using var connection = new NpgsqlConnection(_localAdminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{_localDatabaseName}\" WITH (FORCE)",
                connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task InitializeLocalPostgresAsync()
    {
        var adminBuilder = LocalPostgresEnvironment.CreateLoopbackBuilder();
        adminBuilder.Database = "postgres";
        _localAdminConnectionString = adminBuilder.ConnectionString;
        _localDatabaseName = $"rolvix_test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(_localAdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE \"{_localDatabaseName}\"",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        adminBuilder.Database = _localDatabaseName;
        Factory = new PostgresAppDbFactory(adminBuilder.ConnectionString);

        await using var db = Factory.Create(new FakeTenantProvider());
        await db.Database.MigrateAsync();
    }
}
