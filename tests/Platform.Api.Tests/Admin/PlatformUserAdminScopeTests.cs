using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

public sealed class PlatformUserAdminScopeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_reads_across_tenants_and_preserves_explicit_filters(bool failClosed)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), failClosed: failClosed);
        await PlatformUserAdminScopeScenarios.ListAsync(db, failClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_removes_memberships_and_preserves_shared_auth(bool failClosed)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), failClosed: failClosed);
        await PlatformUserAdminScopeScenarios.DeleteAsync(db, failClosed);
    }
}

public sealed class PlatformUserAdminScopePostgresTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task List_reads_across_tenants_and_preserves_explicit_filters()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");
        foreach (var failClosed in new[] { false, true })
        {
            await using var db = factory.Create(new FakeTenantProvider(), failClosed);
            await PlatformUserAdminScopeScenarios.ListAsync(db, failClosed);
        }
    }

    [DockerFact]
    public async Task Delete_removes_memberships_and_preserves_shared_auth()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");
        foreach (var failClosed in new[] { false, true })
        {
            await using var db = factory.Create(new FakeTenantProvider(), failClosed);
            await PlatformUserAdminScopeScenarios.DeleteAsync(db, failClosed);
        }
    }
}

internal static class PlatformUserAdminScopeScenarios
{
    internal static async Task DeleteAsync(AppDbContext db, bool failClosed)
    {
        var (alice, bob, admin) = await SeedAsync(db);
        var otherMembership = new User(bob.TenantId, alice.SupabaseAuthId, "Alice", alice.Email);
        db.Users.Add(otherMembership);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await AssertUnscopedReadsAsync(db, failClosed);
        var auth = new RecordingPasswordAuthAdmin();
        var service = CreateService(db, auth);

        await service.DeleteAsync(alice.Id, null, CancellationToken.None);

        var remaining = await service.ListAsync(null, null, CancellationToken.None);
        Assert.DoesNotContain(remaining, u => u.Id == alice.Id);
        Assert.Contains(remaining, u => u.Id == otherMembership.Id);
        Assert.Empty(auth.Deleted);
        await AssertUnscopedReadsAsync(db, failClosed);

        await service.DeleteAsync(otherMembership.Id, null, CancellationToken.None);
        Assert.Equal(alice.SupabaseAuthId, Assert.Single(auth.Deleted));
        Assert.DoesNotContain(await service.ListAsync(null, null, CancellationToken.None),
            u => u.Id == otherMembership.Id);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.DeleteAsync(alice.Id, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(bob.Id, bob.SupabaseAuthId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(admin.Id, null, CancellationToken.None));
        Assert.Equal(bob.Id, Assert.Single(await service.ListAsync(null, bob.TenantId, CancellationToken.None)).Id);
        Assert.Single(auth.Deleted);
        await AssertUnscopedReadsAsync(db, failClosed);
    }

    internal static async Task ListAsync(AppDbContext db, bool failClosed)
    {
        var (alice, bob, admin) = await SeedAsync(db);
        await AssertUnscopedReadsAsync(db, failClosed);
        var service = CreateService(db, new RecordingPasswordAuthAdmin());

        var users = await service.ListAsync(null, null, CancellationToken.None);

        // Limit the assertion to this scenario's tenants: the Postgres fixture is shared between cases.
        Assert.Equal(new[] { alice.Id, bob.Id }, users
            .Where(u => u.TenantId == alice.TenantId || u.TenantId == bob.TenantId)
            .Select(u => u.Id));
        Assert.DoesNotContain(users, u => u.Id == admin.Id);
        var filtered = await service.ListAsync(null, bob.TenantId, CancellationToken.None);
        Assert.Equal(bob.Id, Assert.Single(filtered).Id);
        Assert.Equal("USER", Assert.Single(Assert.Single(filtered).Roles));
        await AssertUnscopedReadsAsync(db, failClosed);

        if (db.Database.IsRelational())
        {
            // ILike is a Postgres operation; InMemory cannot evaluate it.
            var named = await service.ListAsync("  bOb  ", bob.TenantId, CancellationToken.None);
            Assert.Equal(bob.Id, Assert.Single(named).Id);
            Assert.Empty(await service.ListAsync("Alice", bob.TenantId, CancellationToken.None));
        }
    }

    private static PlatformUserAdminService CreateService(AppDbContext db, RecordingPasswordAuthAdmin auth) =>
        new(db, auth, new FakePlatformAdminChecker("platform@example.test"),
            NullLogger<PlatformUserAdminService>.Instance);

    private static async Task AssertUnscopedReadsAsync(AppDbContext db, bool failClosed)
    {
        Assert.False(PlatformScope.IsActive);
        if (failClosed)
        {
            Assert.Equal(0, await db.Users.AsNoTracking().CountAsync());
        }
        else
        {
            Assert.True(await db.Users.AsNoTracking().AnyAsync());
        }
    }

    private static async Task<(User Alice, User Bob, User Admin)> SeedAsync(AppDbContext db)
    {
        var tenantA = new Tenant("Tenant A", Guid.NewGuid().ToString("N")[..20]);
        var tenantB = new Tenant("Tenant B", Guid.NewGuid().ToString("N")[..20]);
        var alice = new User(tenantA.Id, Guid.NewGuid().ToString(), "Alice", "alice@example.test");
        var bob = new User(tenantB.Id, Guid.NewGuid().ToString(), "Bob", "bob@example.test");
        var admin = new User(tenantA.Id, Guid.NewGuid().ToString(), "Platform Admin", "platform@example.test");
        var role = new Role(tenantB.Id, "USER");
        db.Tenants.AddRange(tenantA, tenantB);
        db.Users.AddRange(alice, bob, admin);
        db.Roles.Add(role);
        db.UserRoles.Add(new UserRole(bob.Id, role.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (alice, bob, admin);
    }
}
