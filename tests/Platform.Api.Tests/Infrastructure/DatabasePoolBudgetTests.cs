using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Api.Jobs;
using Platform.Core.Infrastructure;

namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// The Supabase session pooler admits a fixed number of clients (pool_size 15) for the
/// whole project. Every API machine opens its own EF and Hangfire pools, so the per-machine
/// caps must fit the machine count, or page bursts fail with EMAXCONNSESSION (PROD 2026-11-13).
/// </summary>
public sealed class DatabasePoolBudgetTests
{
    private const int SessionPoolerClients = 15;
    private const int ApiMachines = 2;

    // One client kept free for migration and ops tooling.
    private const int ReservedClients = 1;

    [Fact]
    public void Default_pools_of_every_api_machine_fit_the_session_pooler()
    {
        var perMachine = DependencyInjection.DefaultEfMaxPoolSize + HangfireExtensions.DefaultMaxPoolSize;

        Assert.True(
            perMachine * ApiMachines <= SessionPoolerClients - ReservedClients,
            $"{ApiMachines} machines x {perMachine} connections exceed {SessionPoolerClients - ReservedClients}.");
    }

    [Fact]
    public void Ef_pool_uses_the_default_cap()
    {
        Assert.Equal(DependencyInjection.DefaultEfMaxPoolSize, EfMaxPoolSize(maxPoolSize: null));
    }

    [Fact]
    public void Ef_pool_uses_a_configured_cap()
    {
        Assert.Equal(2, EfMaxPoolSize(maxPoolSize: 2));
    }

    [Fact]
    public void Ef_pool_rejects_a_cap_below_one()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddCorePersistence("Host=localhost;Database=unused", maxPoolSize: 0));
    }

    [Fact]
    public void Hangfire_pool_uses_the_default_cap()
    {
        var bounded = HangfireExtensions.BoundedConnectionString("Host=localhost;Database=unused", maxPoolSize: null);

        Assert.Equal(HangfireExtensions.DefaultMaxPoolSize, new NpgsqlConnectionStringBuilder(bounded).MaxPoolSize);
    }

    [Fact]
    public void Hangfire_pool_uses_a_configured_cap_and_rejects_below_one()
    {
        var bounded = HangfireExtensions.BoundedConnectionString("Host=localhost;Database=unused", maxPoolSize: 1);

        Assert.Equal(1, new NpgsqlConnectionStringBuilder(bounded).MaxPoolSize);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HangfireExtensions.BoundedConnectionString("Host=localhost;Database=unused", maxPoolSize: 0));
    }

    private static int EfMaxPoolSize(int? maxPoolSize)
    {
        var services = new ServiceCollection();
        services.AddCorePersistence("Host=localhost;Database=unused", maxPoolSize);
        using var provider = services.BuildServiceProvider();
        var dataSource = provider.GetRequiredService<NpgsqlDataSource>();
        return new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize;
    }
}
