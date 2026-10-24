using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

public sealed class TenantUserAdminFailClosedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resend_rotates_only_the_owning_tenants_invite(bool failClosed)
    {
        await using var h = await Harness.CreateAsync(failClosed);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            h.Service.ResendInviteAsync(h.A.Id, h.InviteB.Id, CancellationToken.None));
        Assert.False(h.Queue.TryRead(out _));

        await h.Service.ResendInviteAsync(h.A.Id, h.InviteA.Id, CancellationToken.None);

        var invites = (await h.Service.ListUsersAndInvitesAsync(h.A.Id, CancellationToken.None)).Invites;
        Assert.Equal(2, invites.Count);
        Assert.NotNull(Assert.Single(invites, i => i.Id == h.InviteA.Id).RevokedAt);
        var replacement = Assert.Single(invites, i => i.Id != h.InviteA.Id);
        Assert.True(replacement.IsPending);
        Assert.True(replacement.ExpiresAt > h.InviteA.ExpiresAt);
        Assert.Equal(h.InviteA.Email, replacement.Email);
        var rotated = await h.Db.UserInvites.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(i => i.Id == replacement.Id);
        Assert.Equal(h.A.Id, rotated.TenantId);
        Assert.NotEqual("token-a", rotated.Token);
        Assert.True(h.Queue.TryRead(out var message));
        Assert.Equal(h.InviteA.Email, message.Recipient);
        Assert.Contains($"/invite?token={rotated.Token}", message.Body, StringComparison.Ordinal);
        Assert.False(h.Queue.TryRead(out _));
        var links = await h.Db.UserInviteRoles.AsNoTracking()
            .Where(r => r.InviteId == replacement.Id).Select(r => r.RoleId).ToListAsync();
        Assert.Equal([h.RoleA.Id], links);
        await h.AssertBUnchangedAsync();
        Assert.False(PlatformScope.IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Promote_grants_a_role_only_to_the_owning_tenants_user(bool failClosed)
    {
        await using var h = await Harness.CreateAsync(failClosed);
        var request = new PromoteTenantUserRequestDto { RoleName = SystemRoles.Admin };
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            h.Service.PromoteAsync(h.A.Id, h.UserB.Id, request, CancellationToken.None));

        await h.Service.PromoteAsync(h.A.Id, h.UserA.Id, request, CancellationToken.None);

        var bundle = await h.Service.ListUsersAndInvitesAsync(h.A.Id, CancellationToken.None);
        Assert.Equal(h.UserA.Id, Assert.Single(bundle.Users).Id);
        Assert.Equal([SystemRoles.Admin], Assert.Single(bundle.Users).Roles);
        await h.AssertBUnchangedAsync();
        Assert.False(PlatformScope.IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_and_revoke_stay_within_the_requested_tenant(bool failClosed)
    {
        await using var h = await Harness.CreateAsync(failClosed);
        var bundle = await h.Service.ListUsersAndInvitesAsync(h.A.Id, CancellationToken.None);
        Assert.Equal(h.UserA.Id, Assert.Single(bundle.Users).Id);
        Assert.Equal(h.InviteA.Id, Assert.Single(bundle.Invites).Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            h.Service.RevokeInviteAsync(h.A.Id, h.InviteB.Id, CancellationToken.None));

        await h.Service.RevokeInviteAsync(h.A.Id, h.InviteA.Id, CancellationToken.None);

        Assert.NotNull(Assert.Single((await h.Service.ListUsersAndInvitesAsync(
            h.A.Id, CancellationToken.None)).Invites).RevokedAt);
        await h.AssertBUnchangedAsync();
        Assert.False(h.Queue.TryRead(out _));
        Assert.False(PlatformScope.IsActive);
    }

    private sealed class Harness(bool failClosed) : IAsyncDisposable
    {
        // Platform admin has no ambient tenant; each service operation must open its own scope.
        public AppDbContext Db { get; } = InMemoryAppDb.Create(new FakeTenantProvider(), failClosed: failClosed);
        public Tenant A { get; } = new("Tenant A", "11111111000191");
        public Tenant B { get; } = new("Tenant B", "22222222000191");
        public User UserA { get; private set; } = null!;
        public User UserB { get; private set; } = null!;
        public UserInvite InviteA { get; private set; } = null!;
        public UserInvite InviteB { get; private set; } = null!;
        public Role RoleA { get; private set; } = null!;
        public Role RoleB { get; private set; } = null!;
        public NotificationQueue Queue { get; } = new();
        public TenantUserAdminService Service { get; private set; } = null!;

        public static async Task<Harness> CreateAsync(bool failClosed)
        {
            var h = new Harness(failClosed);
            h.UserA = new User(h.A.Id, "auth-a", "Alice", "alice@test.local");
            h.UserB = new User(h.B.Id, "auth-b", "Bob", "bob@test.local");
            h.InviteA = new UserInvite(h.A.Id, "invite-a@test.local", "Ada", SystemRoles.User,
                "token-a", DateTimeOffset.UtcNow.AddDays(2));
            h.InviteB = new UserInvite(h.B.Id, "invite-b@test.local", "Bea", SystemRoles.User,
                "token-b", DateTimeOffset.UtcNow.AddDays(2));
            h.RoleA = new Role(h.A.Id, SystemRoles.User);
            h.RoleB = new Role(h.B.Id, SystemRoles.User);
            h.Db.Tenants.AddRange(h.A, h.B);
            h.Db.Users.AddRange(h.UserA, h.UserB);
            h.Db.Roles.AddRange(h.RoleA, h.RoleB);
            h.Db.UserRoles.Add(new UserRole(h.UserB.Id, h.RoleB.Id));
            h.Db.UserInvites.AddRange(h.InviteA, h.InviteB);
            h.Db.UserInviteRoles.AddRange(new UserInviteRole(h.InviteA.Id, h.RoleA.Id),
                new UserInviteRole(h.InviteB.Id, h.RoleB.Id));
            await h.Db.SaveChangesAsync();
            h.Db.ChangeTracker.Clear();
            h.Service = new TenantUserAdminService(h.Db, new FakeSupabaseAuthAdminClient(), h.Queue,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:FrontendBaseUrl"] = "https://app.example.test",
                }).Build(), new FakeHostEnvironment(), new FakeTrialGuard(), new FakePlatformAdminChecker(),
                NullLogger<TenantUserAdminService>.Instance);
            return h;
        }

        public async Task AssertBUnchangedAsync()
        {
            var bundle = await Service.ListUsersAndInvitesAsync(B.Id, CancellationToken.None);
            var user = Assert.Single(bundle.Users);
            Assert.Equal(UserB.Id, user.Id);
            Assert.Equal([SystemRoles.User], user.Roles);
            var invite = Assert.Single(bundle.Invites);
            Assert.Equal(InviteB.Id, invite.Id);
            Assert.True(invite.IsPending);
            Assert.Equal(InviteB.ExpiresAt, invite.ExpiresAt);
            var persisted = await Db.UserInvites.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == InviteB.Id);
            Assert.Equal("token-b", persisted.Token);
            Assert.Equal([RoleB.Id], await Db.UserInviteRoles.AsNoTracking()
                .Where(r => r.InviteId == InviteB.Id).Select(r => r.RoleId).ToListAsync());
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
