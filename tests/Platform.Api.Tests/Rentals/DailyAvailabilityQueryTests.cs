using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Infrastructure;

namespace Platform.Api.Tests.Rentals;

public sealed class DailyAvailabilityQueryTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task PostgreSql_projection_uses_eight_reads_for_one_or_many_resources_and_never_writes()
    {
        Assert.NotNull(fixture.Factory);
        await using var h = await AvailabilityHarness.CreateAsync();
        var other = await h.AddResourceAsync(h.Tenant, h.Unit, "Resource B");
        await using (var seed = fixture.Factory.Create(h.TenantProvider))
        {
            seed.AddRange(h.Db.ChangeTracker.Entries().Select(entry => entry.Entity));
            await seed.SaveChangesAsync();
        }
        var counter = new ReadCounter();
        await using var db = fixture.Factory.Create(h.TenantProvider, counter);
        var service = new DailyAvailabilityService(db, h.TenantProvider, h.Clock);
        var one = await service.GetAsync(AvailabilityHarness.Day, [h.Primary.Id], null, 1, null, default);
        Assert.Single(one.Resources);
        Assert.Equal(8, counter.Reads);
        counter.Reads = 0;
        var many = await service.GetAsync(AvailabilityHarness.Day, [h.Primary.Id, other.Id], null, 1, null, default);
        Assert.Equal(2, many.Resources.Count);
        Assert.Equal(8, counter.Reads);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Reads { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Assert.StartsWith("SELECT", command.CommandText.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Reads++;
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Availability may not execute write commands.");
    }
}
