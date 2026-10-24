using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Jobs;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Tenancy;

/// <summary>
/// Passo 7: <c>Tenancy:FailClosed</c>. Off keeps the legacy filter (null tenant = every tenant);
/// on, null tenant outside a PlatformScope reads nothing and a PlatformScope reads every tenant.
/// </summary>
public sealed class FailClosedTenantFilterTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_reads_only_its_own_rows(bool failClosed)
    {
        var provider = new FakeTenantProvider { TenantId = TenantA };
        await using var db = await SeedAsync(provider, failClosed);

        Assert.Equal([TenantA], await TenantIdsAsync(db));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public async Task No_tenant_outside_platform_scope(bool failClosed, int expectedRows)
    {
        await using var db = await SeedAsync(new FakeTenantProvider(), failClosed);

        Assert.Equal(expectedRows, (await TenantIdsAsync(db)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Platform_scope_without_tenant_reads_every_tenant(bool failClosed)
    {
        await using var db = await SeedAsync(new FakeTenantProvider(), failClosed);

        using (PlatformScope.Begin(NullLogger.Instance, "test"))
        {
            Assert.Equal(2, (await TenantIdsAsync(db)).Count);
        }

        Assert.Equal(failClosed ? 0 : 2, (await TenantIdsAsync(db)).Count);
    }

    [Theory]
    [InlineData(false, 1)] // legacy: provider tenant still filters inside the scope
    [InlineData(true, 2)] // ITenantContext.Mode == Platform: no filter
    public async Task Platform_scope_with_provider_tenant(bool failClosed, int expectedRows)
    {
        await using var db = await SeedAsync(new FakeTenantProvider { TenantId = TenantA }, failClosed);

        using (PlatformScope.Begin(NullLogger.Instance, "test"))
        {
            Assert.Equal(expectedRows, (await TenantIdsAsync(db)).Count);
        }

        Assert.Equal([TenantA], await TenantIdsAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_context_follows_provider_changes(bool failClosed)
    {
        // Ambient binders set the tenant mid-request; the filter must not cache the first value.
        var provider = new FakeTenantProvider();
        await using var db = await SeedAsync(provider, failClosed);

        Assert.Equal(failClosed ? 0 : 2, (await TenantIdsAsync(db)).Count);
        provider.TenantId = TenantB;
        Assert.Equal([TenantB], await TenantIdsAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataRetentionJob_purges_every_tenant(bool failClosed)
    {
        // The job reads without IgnoreQueryFilters; under fail-closed only its PlatformScope keeps it working.
        var databaseName = $"fail-closed-{Guid.NewGuid():N}";
        await using (var seed = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            seed.AssetCategories.AddRange(
                Category(TenantA, DateTimeOffset.UtcNow.AddDays(-1)),
                Category(TenantB, DateTimeOffset.UtcNow.AddDays(-1)));
            await seed.SaveChangesAsync();
        }

        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName, failClosed: failClosed);
        await new DataRetentionJob(db, NullLogger<DataRetentionJob>.Instance).ExecuteAsync(CancellationToken.None);

        await using var check = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        Assert.Empty(await check.AssetCategories.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Container_passes_tenancy_options_to_the_context()
    {
        var databaseName = $"fail-closed-{Guid.NewGuid():N}";
        await using (var seed = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            seed.AssetCategories.Add(Category(TenantA));
            await seed.SaveChangesAsync();
        }

        await using var services = new ServiceCollection()
            .Configure<TenancyOptions>(options => options.FailClosed = true)
            .AddSingleton<ITenantProvider>(new FakeTenantProvider())
            .AddSingleton(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options)
            .AddScoped<TestAppDbContext>()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();

        Assert.Empty(await TenantIdsAsync(scope.ServiceProvider.GetRequiredService<TestAppDbContext>()));
    }

    private static async Task<AppDbContext> SeedAsync(FakeTenantProvider provider, bool failClosed)
    {
        var databaseName = $"fail-closed-{Guid.NewGuid():N}";
        await using (var seed = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            seed.AssetCategories.AddRange(Category(TenantA), Category(TenantB));
            await seed.SaveChangesAsync();
        }

        return InMemoryAppDb.Create(provider, databaseName, failClosed: failClosed);
    }

    private static async Task<List<Guid>> TenantIdsAsync(AppDbContext db) =>
        await db.AssetCategories.AsNoTracking().Select(c => c.TenantId).OrderBy(id => id).ToListAsync();

    private static AssetCategory Category(Guid tenantId, DateTimeOffset? scheduledDeletionAt = null) =>
        new() { TenantId = tenantId, Name = $"cat-{Guid.NewGuid():N}", ScheduledDeletionAt = scheduledDeletionAt };
}
