using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Rentals;

public sealed class OccupancyKindDefaultsRaceTests
{
    private const string KeyConstraint = "ix_occupancy_kinds_tenant_id_key";

    [Fact]
    public async Task EnsureDefaults_when_a_concurrent_request_seeded_first_does_not_throw()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        var databaseName = $"occupancy-race-{Guid.NewGuid():N}";
        await using var dbB = InMemoryAppDb.Create(tenantProvider, databaseName);
        var interceptor = new ConflictOnFirstSave(
            () => new OccupancyKindService(dbB, tenantProvider).EnsureDefaultsAsync(CancellationToken.None),
            KeyConstraint);
        await using var dbA = InMemoryAppDb.Create(tenantProvider, databaseName, interceptors: [interceptor]);
        var service = new OccupancyKindService(dbA, tenantProvider);

        await service.EnsureDefaultsAsync(CancellationToken.None);

        Assert.True(interceptor.Fired);
        Assert.DoesNotContain(
            dbA.ChangeTracker.Entries<OccupancyKind>(),
            e => e.State == EntityState.Added);
        Assert.Equal(3, await dbA.OccupancyKinds.CountAsync());
        var listed = await service.ListAsync(CancellationToken.None);
        Assert.Equal(["open", "closed", "lesson"], listed.Select(k => k.Key).ToArray());
    }

    [Fact]
    public async Task EnsureDefaults_other_unique_violation_still_throws()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        var databaseName = $"occupancy-race-{Guid.NewGuid():N}";
        await using var dbB = InMemoryAppDb.Create(tenantProvider, databaseName);
        var interceptor = new ConflictOnFirstSave(
            () => new OccupancyKindService(dbB, tenantProvider).EnsureDefaultsAsync(CancellationToken.None),
            "ux_users_email");
        await using var dbA = InMemoryAppDb.Create(tenantProvider, databaseName, interceptors: [interceptor]);
        var service = new OccupancyKindService(dbA, tenantProvider);

        await Assert.ThrowsAsync<DbUpdateException>(
            () => service.EnsureDefaultsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnsureDefaults_seeds_a_fresh_tenant_once()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var service = new OccupancyKindService(db, tenantProvider);

        await service.EnsureDefaultsAsync(CancellationToken.None);
        await service.EnsureDefaultsAsync(CancellationToken.None);

        Assert.Equal(3, await db.OccupancyKinds.CountAsync());
    }

    private sealed class ConflictOnFirstSave(Func<Task> seedElsewhere, string constraintName)
        : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Fired)
            {
                return result;
            }

            Fired = true;
            await seedElsewhere();
            throw new DbUpdateException(
                "conflict",
                new PostgresException(
                    $"duplicate key value violates unique constraint \"{constraintName}\"",
                    "ERROR",
                    "ERROR",
                    PostgresErrorCodes.UniqueViolation,
                    constraintName: constraintName));
        }
    }
}
