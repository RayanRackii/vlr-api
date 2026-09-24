using Microsoft.EntityFrameworkCore;
using Platform.Api.Tests.Fakes;
using Testcontainers.PostgreSql;

namespace Platform.Api.Tests.Infrastructure;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public PostgresAppDbFactory? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("ROLVIX_TEST_POSTGRES");
        if (!string.IsNullOrWhiteSpace(external))
        {
            Factory = new PostgresAppDbFactory(external);
            await using var externalDb = Factory.Create(new FakeTenantProvider());
            await externalDb.Database.MigrateAsync();
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
    }
}
