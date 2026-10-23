using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence.Seed;
using System.Data.Common;

namespace Platform.Api.Tests.ResourceVisuals;

public sealed class ResourceSegmentsConcurrencyTests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task Concurrent_admin_and_tenant_segment_replacements_serialize_and_never_union_sets()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        var tenant = new Tenant("Segment concurrency", Guid.NewGuid().ToString("N")[..20]);
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using (var seed = factory.Create(provider))
        {
            seed.Add(tenant);
            await seed.SaveChangesAsync();
        }
        await using var blocker = factory.Create(provider);
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM core.tenants WHERE id = {tenant.Id} FOR UPDATE");
        var firstAttempt = new LockAttempt();
        var secondAttempt = new LockAttempt();
        await using var firstDb = factory.Create(provider, firstAttempt);
        await using var secondDb = factory.Create(provider, secondAttempt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstId = ResourceVisualCatalogSeed.StableId("tag:sports-courts");
        var secondId = ResourceVisualCatalogSeed.StableId("tag:health");
        var first = Service(firstDb, provider).ReplaceSegmentsAsync(null, new([firstId]), timeout.Token);
        var second = Service(secondDb, provider).ReplaceSegmentsAsync(tenant.Id, new([secondId]), timeout.Token);
        try
        {
            await Task.WhenAll(firstAttempt.Started.Task, secondAttempt.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); }
        var results = await Task.WhenAll(first, second);
        Assert.Equal(firstId, Assert.Single(results[0]).Id);
        Assert.Equal(secondId, Assert.Single(results[1]).Id);
        await using var verify = factory.Create(provider);
        var final = await verify.Set<TenantResourceSegment>().Where(s => s.TenantId == tenant.Id).Select(s => s.TagId).ToArrayAsync();
        Assert.Contains(Assert.Single(final), new[] { firstId, secondId });
    }

    private static ResourceVisualCatalogService Service(Platform.Core.Infrastructure.Persistence.AppDbContext db, FakeTenantProvider provider) =>
        new(db, provider, new FakeTrialGuard(), new Platform.Api.Modules.Rentals.Services.RentalVisualDefaults(), NullLogger<ResourceVisualCatalogService>.Instance);

    private sealed class LockAttempt : DbCommandInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)) Started.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
