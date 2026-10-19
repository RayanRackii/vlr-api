using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

public sealed class ExistingAccountInviteTests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private const string OriginalPassword = "original-password-1";
    private const string AttackerPassword = "attacker-password-1";

    [Fact]
    public async Task Anonymous_accept_does_not_change_an_existing_password_hash_or_the_other_tenant()
    {
        await using var harness = await InviteHarness.CreateAsync();
        var originalHash = harness.Auth.HashOf(harness.VictimAuthId);
        var originalRoles = await RoleIdsAsync(harness, harness.Victim.Id);

        var ex = await Assert.ThrowsAsync<InviteAcceptanceException>(() => harness.AcceptAsync(
            harness.Invite.Token,
            AttackerPassword));

        Assert.Equal(InviteAcceptanceCodes.ExistingAccountSignInRequired, ex.Code);
        Assert.Equal(StatusCodes.Status409Conflict, ex.StatusCode);
        Assert.Equal(originalHash, harness.Auth.HashOf(harness.VictimAuthId));
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.MetadataUpdates);
        Assert.Equal(harness.Home.Id, harness.Auth.TenantOf(harness.VictimAuthId));

        var home = await ReloadVictimAsync(harness);
        Assert.Equal("Ada Victim", home.FullName);
        Assert.Equal(harness.VictimAuthId, home.SupabaseAuthId);
        Assert.True(home.IsActive);
        Assert.Equal(originalRoles, await RoleIdsAsync(harness, home.Id));
        Assert.Null(await harness.Db.UserInvites.IgnoreQueryFilters()
            .Where(invite => invite.Id == harness.Invite.Id)
            .Select(invite => invite.AcceptedAt)
            .SingleAsync());
        Assert.Equal(0, await MembershipCountAsync(harness, harness.Inviter.Id));
    }

    [Fact]
    public async Task Local_membership_without_an_auth_email_hit_still_blocks_password_creation()
    {
        await using var harness = await InviteHarness.CreateAsync(registerAuthEmail: false);

        var ex = await Assert.ThrowsAsync<InviteAcceptanceException>(() => harness.AcceptAsync(
            harness.Invite.Token,
            AttackerPassword));

        Assert.Equal(InviteAcceptanceCodes.ExistingAccountSignInRequired, ex.Code);
        Assert.Equal(RecordingPasswordAuthAdmin.Hash(OriginalPassword), harness.Auth.HashOf(harness.VictimAuthId));
        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Equal(0, await MembershipCountAsync(harness, harness.Inviter.Id));
    }

    [Fact]
    public async Task Signed_in_owner_links_only_the_inviting_tenant_and_keeps_the_password_hash()
    {
        await using var harness = await InviteHarness.CreateAsync();
        var originalHash = harness.Auth.HashOf(harness.VictimAuthId);
        var homeRoleIds = await RoleIdsAsync(harness, harness.Victim.Id);

        var linked = await harness.AcceptAsync(
            harness.Invite.Token,
            AttackerPassword,
            Staff(harness.VictimEmail, harness.VictimAuthId, harness.Home.Id));

        Assert.True(linked.ExistingAccount);
        Assert.Equal(harness.Inviter.Id, linked.TenantId);
        Assert.NotEqual(harness.Victim.Id, linked.UserId);
        Assert.Equal(originalHash, harness.Auth.HashOf(harness.VictimAuthId));
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.MetadataUpdates);
        Assert.Equal(harness.Home.Id, harness.Auth.TenantOf(harness.VictimAuthId));

        var home = await ReloadVictimAsync(harness);
        Assert.Equal("Ada Victim", home.FullName);
        Assert.Equal(homeRoleIds, await RoleIdsAsync(harness, home.Id));
        Assert.DoesNotContain(harness.InvitedRole.Id, await RoleIdsAsync(harness, home.Id));

        harness.TenantProvider.TenantId = harness.Home.Id;
        var visibleAtHome = await harness.Db.Users.Select(user => user.Id).ToListAsync();
        Assert.Equal([harness.Victim.Id], visibleAtHome);

        var joined = await harness.Db.Users
            .IgnoreQueryFilters()
            .Include(user => user.UserRoles)
            .SingleAsync(user => user.Id == linked.UserId);
        Assert.Equal(harness.Inviter.Id, joined.TenantId);
        Assert.Equal(harness.VictimAuthId, joined.SupabaseAuthId);
        Assert.Equal(harness.VictimEmail, joined.Email);
        var joinedRole = Assert.Single(joined.UserRoles);
        Assert.Equal(harness.InvitedRole.Id, joinedRole.RoleId);
        Assert.DoesNotContain(joined.UserRoles, role => role.RoleId == harness.HomeRole.Id);
    }

    [Fact]
    public async Task Other_staff_session_and_customer_session_do_not_link_or_reset_the_password()
    {
        await using var harness = await InviteHarness.CreateAsync();
        var originalHash = harness.Auth.HashOf(harness.VictimAuthId);

        var mismatch = await Assert.ThrowsAsync<InviteAcceptanceException>(() => harness.AcceptAsync(
            harness.Invite.Token,
            AttackerPassword,
            Staff("other@club.test", Guid.NewGuid().ToString(), harness.Inviter.Id)));
        Assert.Equal(InviteAcceptanceCodes.AccountMismatch, mismatch.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, mismatch.StatusCode);

        var customer = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", harness.VictimAuthId),
                new Claim("email", harness.VictimEmail),
                new Claim("customer_id", Guid.NewGuid().ToString()),
            ],
            "Test"));
        var customerResult = await Assert.ThrowsAsync<InviteAcceptanceException>(() => harness.AcceptAsync(
            harness.Invite.Token,
            AttackerPassword,
            customer));
        Assert.Equal(InviteAcceptanceCodes.ExistingAccountSignInRequired, customerResult.Code);

        Assert.Equal(originalHash, harness.Auth.HashOf(harness.VictimAuthId));
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.MetadataUpdates);
        Assert.Equal(0, await MembershipCountAsync(harness, harness.Inviter.Id));
        Assert.Null(await harness.Db.UserInvites.IgnoreQueryFilters()
            .Where(invite => invite.Id == harness.Invite.Id)
            .Select(item => item.AcceptedAt)
            .SingleAsync());
    }

    [Fact]
    public async Task Expired_revoked_and_reused_tokens_do_not_change_credentials_or_memberships()
    {
        await using var harness = await InviteHarness.CreateAsync();
        var originalHash = harness.Auth.HashOf(harness.VictimAuthId);
        var owner = Staff(harness.VictimEmail, harness.VictimAuthId, harness.Home.Id);

        harness.Invite.Revoke();
        await harness.Db.SaveChangesAsync();
        var revoked = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AcceptAsync(
            harness.Invite.Token,
            AttackerPassword,
            owner));
        Assert.Equal("Invite was revoked.", revoked.Message);

        var expired = NewInvite(harness, "expired-token", DateTimeOffset.UtcNow.AddMinutes(-5));
        harness.Db.UserInvites.Add(expired);
        harness.Db.UserInviteRoles.Add(new UserInviteRole(expired.Id, harness.InvitedRole.Id));
        await harness.Db.SaveChangesAsync();
        var expiredResult = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AcceptAsync(
            expired.Token,
            AttackerPassword,
            owner));
        Assert.Equal("Invite has expired.", expiredResult.Message);

        var once = NewInvite(harness, "once-token", DateTimeOffset.UtcNow.AddDays(1));
        harness.Db.UserInvites.Add(once);
        harness.Db.UserInviteRoles.Add(new UserInviteRole(once.Id, harness.InvitedRole.Id));
        await harness.Db.SaveChangesAsync();
        await harness.AcceptAsync(once.Token, AttackerPassword, owner);
        var reuse = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AcceptAsync(
            once.Token,
            AttackerPassword,
            owner));
        Assert.Equal("Invite was already accepted.", reuse.Message);

        Assert.Equal(originalHash, harness.Auth.HashOf(harness.VictimAuthId));
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.MetadataUpdates);
        Assert.Equal(1, await MembershipCountAsync(harness, harness.Inviter.Id));
        Assert.Equal(1, await harness.Db.Users.IgnoreQueryFilters().CountAsync(user => user.TenantId == harness.Home.Id));
    }

    [Fact]
    public async Task Invite_role_from_another_tenant_is_not_applied_to_either_membership()
    {
        await using var harness = await InviteHarness.CreateAsync();
        var foreign = NewInvite(harness, "foreign-role-token", DateTimeOffset.UtcNow.AddDays(1));
        harness.Db.UserInvites.Add(foreign);
        harness.Db.UserInviteRoles.Add(new UserInviteRole(foreign.Id, harness.HomeRole.Id));
        await harness.Db.SaveChangesAsync();

        var linked = await harness.AcceptAsync(
            foreign.Token,
            password: null,
            Staff(harness.VictimEmail, harness.VictimAuthId, harness.Home.Id));

        var joinedRoles = await RoleIdsAsync(harness, linked.UserId);
        Assert.DoesNotContain(harness.HomeRole.Id, joinedRoles);
        var joinedRole = await harness.Db.Roles.IgnoreQueryFilters()
            .SingleAsync(role => joinedRoles.Contains(role.Id));
        Assert.Equal(harness.Inviter.Id, joinedRole.TenantId);
        Assert.Equal(SystemRoles.Technician, joinedRole.Name);
        Assert.Equal([harness.HomeRole.Id], await RoleIdsAsync(harness, harness.Victim.Id));
        Assert.Empty(harness.Auth.PasswordsSet);
    }

    [Fact]
    public async Task New_user_invite_still_sets_the_initial_password_and_rejects_reuse_and_expiry()
    {
        await using var harness = await InviteHarness.CreateAsync(withVictim: false);
        const string chosen = "brand-new-password-1";

        var expired = NewInvite(harness, "new-expired", DateTimeOffset.UtcNow.AddMinutes(-1), "new@club.test");
        harness.Db.UserInvites.Add(expired);
        harness.Db.UserInviteRoles.Add(new UserInviteRole(expired.Id, harness.InvitedRole.Id));
        await harness.Db.SaveChangesAsync();
        var expiredResult = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AcceptAsync(
            expired.Token,
            chosen));
        Assert.Equal("Invite has expired.", expiredResult.Message);
        Assert.Empty(harness.Auth.Created);

        var created = await harness.AcceptAsync(harness.Invite.Token, chosen);
        Assert.False(created.ExistingAccount);
        var authUser = Assert.Single(harness.Auth.Created);
        Assert.Equal("new@club.test", authUser.Email);
        Assert.Equal(chosen, authUser.Password);
        Assert.Equal(RecordingPasswordAuthAdmin.Hash(chosen), harness.Auth.HashOf(authUser.UserId));
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Equal([(authUser.UserId, harness.Inviter.Id)], harness.Auth.MetadataUpdates);

        var user = await harness.Db.Users.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(created.UserId, user.Id);
        Assert.Equal(harness.Inviter.Id, user.TenantId);
        Assert.Equal(authUser.UserId, user.SupabaseAuthId);
        Assert.Equal([harness.InvitedRole.Id], await RoleIdsAsync(harness, user.Id));

        var reuse = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AcceptAsync(
            harness.Invite.Token,
            "another-password-1"));
        Assert.Equal("Invite was already accepted.", reuse.Message);
        Assert.Single(harness.Auth.Created);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Equal(RecordingPasswordAuthAdmin.Hash(chosen), harness.Auth.HashOf(authUser.UserId));
    }

    [Fact]
    public async Task Preview_reports_existing_account_without_changing_the_password_hash()
    {
        await using var harness = await InviteHarness.CreateAsync();
        var originalHash = harness.Auth.HashOf(harness.VictimAuthId);

        var preview = await harness.Service.PreviewInviteAsync(harness.Invite.Token, CancellationToken.None);

        Assert.True(preview.ExistingAccount);
        Assert.Equal(harness.VictimEmail, preview.Email);
        Assert.Equal(harness.Inviter.Id, preview.TenantId);
        Assert.Equal("Inviter", preview.TenantName);
        Assert.Equal(originalHash, harness.Auth.HashOf(harness.VictimAuthId));
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.MetadataUpdates);
    }

    [DockerFact]
    public Task New_account_and_existing_account_accepts_of_the_same_token_are_serialized() =>
        AssertConcurrentAcceptsAsync(sameToken: true);

    [DockerFact]
    public Task Accepts_of_different_invites_for_the_same_email_are_serialized() =>
        AssertConcurrentAcceptsAsync(sameToken: false);

    [DockerFact]
    public async Task Failed_provisioning_preserves_an_auth_user_with_an_inactive_membership_in_another_tenant()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL is required.");
        await using var harness = await InviteHarness.CreateAsync(withVictim: false, factory: factory);
        var failure = new InvalidOperationException("Provisioning failed after another membership was linked.");
        harness.Auth.AfterMetadataUpdate = async cancellationToken =>
        {
            await using var linked = factory.Create(new FakeTenantProvider { TenantId = harness.Home.Id });
            var membership = new User(harness.Home.Id, harness.Auth.NextUserId, "Linked", harness.Invite.Email);
            membership.Deactivate();
            linked.Users.Add(membership);
            await linked.SaveChangesAsync(cancellationToken);
            throw failure;
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.AcceptAsync(harness.Invite.Token, OriginalPassword));

        Assert.Same(failure, thrown);
        Assert.True(await harness.Auth.UserExistsAsync(harness.Auth.NextUserId));
        Assert.Empty(harness.Auth.Deleted);
        await using var verify = factory.Create(new FakeTenantProvider());
        Assert.Equal(1, await verify.Users.CountAsync(user => user.SupabaseAuthId == harness.Auth.NextUserId));
        Assert.Equal(0, await verify.Users.CountAsync(user => user.TenantId == harness.Inviter.Id));
        Assert.Null((await verify.UserInvites.SingleAsync(invite => invite.Id == harness.Invite.Id)).AcceptedAt);
    }

    [DockerFact]
    public async Task Failed_provisioning_without_memberships_cleans_up_and_leaves_the_token_reusable()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL is required.");
        await using var harness = await InviteHarness.CreateAsync(withVictim: false, factory: factory);
        using var cancellation = new CancellationTokenSource();
        harness.Auth.AfterMetadataUpdate = cancellationToken =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.AcceptInviteAsync(
            new AcceptInviteRequestDto { Token = harness.Invite.Token, Password = OriginalPassword },
            cancellation.Token));

        Assert.Equal([harness.Auth.NextUserId], harness.Auth.Deleted);
        Assert.False(await harness.Auth.UserExistsAsync(harness.Auth.NextUserId));
        await using var retry = factory.Create(harness.TenantProvider);
        Assert.Null((await retry.UserInvites.SingleAsync(invite => invite.Id == harness.Invite.Id)).AcceptedAt);
        harness.Auth.AfterMetadataUpdate = null;
        var accepted = await CreateService(retry, harness.Auth).AcceptInviteAsync(
            new AcceptInviteRequestDto { Token = harness.Invite.Token, Password = OriginalPassword },
            CancellationToken.None);
        Assert.False(accepted.ExistingAccount);
        Assert.True(await harness.Auth.UserExistsAsync(harness.Auth.NextUserId));
    }

    private async Task AssertConcurrentAcceptsAsync(bool sameToken)
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL is required.");
        await using var harness = await InviteHarness.CreateAsync(withVictim: false, factory: factory);
        var secondToken = harness.Invite.Token;
        if (!sameToken)
        {
            var secondInvite = new UserInvite(
                harness.Home.Id, harness.Invite.Email, "Second membership", SystemRoles.Admin,
                Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddDays(1));
            harness.Db.UserInvites.Add(secondInvite);
            harness.Db.UserInviteRoles.Add(new UserInviteRole(secondInvite.Id, harness.HomeRole.Id));
            await harness.Db.SaveChangesAsync();
            secondToken = secondInvite.Token;
        }

        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Auth.AfterMetadataUpdate = async cancellationToken =>
        {
            created.SetResult();
            await resume.Task.WaitAsync(cancellationToken);
        };
        var first = harness.AcceptAsync(harness.Invite.Token, OriginalPassword);
        Task<AcceptInviteResponseDto>? second = null;
        await using var secondDb = factory.Create(harness.TenantProvider);
        try
        {
            await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await secondDb.Database.OpenConnectionAsync();
            await using var command = secondDb.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT pg_backend_pid()";
            var backendPid = Convert.ToInt32(await command.ExecuteScalarAsync());
            second = CreateService(secondDb, harness.Auth).AcceptInviteAsync(
                new AcceptInviteRequestDto { Token = secondToken },
                CancellationToken.None,
                Staff(harness.Invite.Email, harness.Auth.NextUserId, harness.Inviter.Id));

            Assert.True(await WaitForAcceptLockAsync(factory, backendPid, second),
                "The second accept did not wait for provisioning to finish.");
            resume.SetResult();
            var accepted = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(accepted.ExistingAccount);
            if (sameToken)
            {
                var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
                Assert.Equal("Invite was already accepted.", rejected.Message);
            }
            else
            {
                Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(10))).ExistingAccount);
            }

            Assert.True(await harness.Auth.UserExistsAsync(harness.Auth.NextUserId));
            Assert.Empty(harness.Auth.Deleted);
            Assert.Empty(harness.Auth.PasswordsSet);
            Assert.Single(harness.Auth.Created);
            Assert.Single(harness.Auth.MetadataUpdates);
            await using var verify = factory.Create(new FakeTenantProvider());
            Assert.Equal(sameToken ? 1 : 2,
                await verify.Users.CountAsync(user => user.SupabaseAuthId == harness.Auth.NextUserId));
            Assert.Equal(accepted.UserId,
                (await verify.UserInvites.SingleAsync(invite => invite.Id == harness.Invite.Id)).CreatedUserId);
        }
        finally
        {
            resume.TrySetResult();
            await Record.ExceptionAsync(async () => await first.WaitAsync(TimeSpan.FromSeconds(10)));
            if (second is not null)
            {
                await Record.ExceptionAsync(async () => await second.WaitAsync(TimeSpan.FromSeconds(10)));
            }
        }
    }

    private static async Task<bool> WaitForAcceptLockAsync(PostgresAppDbFactory factory, int backendPid, Task accept)
    {
        await using var observer = factory.Create(new FakeTenantProvider());
        await observer.Database.OpenConnectionAsync();
        await using var command = observer.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT wait_event_type = 'Lock' FROM pg_stat_activity WHERE pid = @pid";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "pid";
        parameter.Value = backendPid;
        command.Parameters.Add(parameter);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !accept.IsCompleted)
        {
            if (await command.ExecuteScalarAsync() is true)
            {
                return true;
            }
            await Task.Delay(25);
        }
        return false;
    }

    private static TenantUserAdminService CreateService(AppDbContext db, RecordingPasswordAuthAdmin auth) =>
        new(
            db, auth, new NotificationQueue(), new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(), new FakeTrialGuard(), new FakePlatformAdminChecker(),
            NullLogger<TenantUserAdminService>.Instance);

    private static ClaimsPrincipal Staff(string email, string authId, Guid tokenTenant) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", authId),
                new Claim("email", email),
                new Claim("app_metadata", $$"""{"tenant_id":"{{tokenTenant}}"}"""),
            ],
            authenticationType: "Test"));

    private static async Task<User> ReloadVictimAsync(InviteHarness harness) =>
        await harness.Db.Users.IgnoreQueryFilters().SingleAsync(user => user.Id == harness.Victim.Id);

    private static async Task<List<Guid>> RoleIdsAsync(InviteHarness harness, Guid userId) =>
        await harness.Db.UserRoles
            .IgnoreQueryFilters()
            .Where(role => role.UserId == userId)
            .Select(role => role.RoleId)
            .OrderBy(id => id)
            .ToListAsync();

    private static async Task<int> MembershipCountAsync(InviteHarness harness, Guid tenantId) =>
        await harness.Db.Users.IgnoreQueryFilters().CountAsync(user => user.TenantId == tenantId);

    private static UserInvite NewInvite(
        InviteHarness harness,
        string token,
        DateTimeOffset expiresAt,
        string? email = null) =>
        new(
            harness.Inviter.Id,
            email ?? harness.VictimEmail,
            "Invited Name",
            SystemRoles.Technician,
            token,
            expiresAt);

    private sealed class InviteHarness : IAsyncDisposable
    {
        private InviteHarness(
            AppDbContext db,
            FakeTenantProvider tenantProvider,
            RecordingPasswordAuthAdmin auth,
            TenantUserAdminService service,
            Tenant home,
            Tenant inviter,
            User victim,
            Role homeRole,
            Role invitedRole,
            UserInvite invite,
            string victimAuthId,
            string victimEmail)
        {
            Db = db;
            TenantProvider = tenantProvider;
            Auth = auth;
            Service = service;
            Home = home;
            Inviter = inviter;
            Victim = victim;
            HomeRole = homeRole;
            InvitedRole = invitedRole;
            Invite = invite;
            VictimAuthId = victimAuthId;
            VictimEmail = victimEmail;
        }

        public AppDbContext Db { get; }

        public FakeTenantProvider TenantProvider { get; }

        public RecordingPasswordAuthAdmin Auth { get; }

        public TenantUserAdminService Service { get; }

        public Tenant Home { get; }

        public Tenant Inviter { get; }

        public User Victim { get; }

        public Role HomeRole { get; }

        public Role InvitedRole { get; }

        public UserInvite Invite { get; }

        public string VictimAuthId { get; }

        public string VictimEmail { get; }

        public static async Task<InviteHarness> CreateAsync(
            bool withVictim = true,
            bool registerAuthEmail = true,
            PostgresAppDbFactory? factory = null)
        {
            var home = new Tenant("Home Club",
                factory is null ? "11111111000191" : Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
                tradeName: "Home");
            var inviter = new Tenant("Inviter Club",
                factory is null ? "22222222000191" : Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
                tradeName: "Inviter");
            var tenantProvider = new FakeTenantProvider { TenantId = inviter.Id };
            var db = factory?.Create(tenantProvider) ?? InMemoryAppDb.Create(tenantProvider);
            var homeRole = new Role(home.Id, SystemRoles.Admin, "Admin", isSystemRole: true);
            var invitedRole = new Role(inviter.Id, SystemRoles.Technician, "Technician", isSystemRole: true);
            const string victimEmail = "victim@club.test";
            var victimAuthId = Guid.NewGuid().ToString();
            var victim = new User(home.Id, victimAuthId, "Ada Victim", victimEmail);
            var inviteEmail = withVictim ? victimEmail : factory is null ? "new@club.test" : $"new-{Guid.NewGuid():N}@club.test";
            var invite = new UserInvite(
                inviter.Id,
                inviteEmail,
                "Invited Name",
                SystemRoles.Technician,
                factory is not null ? Guid.NewGuid().ToString("N") : withVictim ? "existing-token" : "new-user-token",
                DateTimeOffset.UtcNow.AddDays(2));

            db.Tenants.AddRange(home, inviter);
            db.Roles.AddRange(homeRole, invitedRole);
            if (withVictim)
            {
                db.Users.Add(victim);
                db.UserRoles.Add(new UserRole(victim.Id, homeRole.Id));
            }

            db.UserInvites.Add(invite);
            db.UserInviteRoles.Add(new UserInviteRole(invite.Id, invitedRole.Id));
            await db.SaveChangesAsync();

            var auth = new RecordingPasswordAuthAdmin();
            if (withVictim)
            {
                auth.RememberCredential(victimAuthId, OriginalPassword);
                if (registerAuthEmail)
                {
                    auth.RegisterEmail(victimEmail, victimAuthId, home.Id);
                }
            }

            var service = CreateService(db, auth);

            return new InviteHarness(
                db,
                tenantProvider,
                auth,
                service,
                home,
                inviter,
                victim,
                homeRole,
                invitedRole,
                invite,
                victimAuthId,
                victimEmail);
        }

        public Task<AcceptInviteResponseDto> AcceptAsync(
            string token,
            string? password,
            ClaimsPrincipal? actor = null) =>
            Service.AcceptInviteAsync(
                new AcceptInviteRequestDto { Token = token, Password = password },
                CancellationToken.None,
                actor);

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
