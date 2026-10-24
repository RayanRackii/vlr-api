using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authentication;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Users;

public sealed class StaffTenantSelectionTests
{
    [Fact]
    public async Task List_returns_only_the_caller_active_memberships_and_select_does_not_change_the_password()
    {
        await using var harness = await SelectionHarness.CreateAsync();
        const string password = "keep-this-password-1";
        harness.Auth.RememberCredential(harness.AuthId, password);
        harness.Auth.RegisterEmail(harness.Email, harness.AuthId, harness.Home.Id);
        var originalHash = harness.Auth.HashOf(harness.AuthId);

        var listed = await harness.Service.ListAsync(
            Staff(harness.Email, harness.AuthId, harness.Home.Id),
            CancellationToken.None);

        Assert.Equal(harness.Home.Id, listed.AccessTokenTenantId);
        Assert.Equal(harness.Home.Id, listed.AuthMetadataTenantId);
        Assert.False(listed.SessionRefreshRequired);
        Assert.Equal([harness.Home.Id, harness.Second.Id], listed.Memberships.Select(item => item.TenantId).ToArray());
        Assert.DoesNotContain(listed.Memberships, item => item.TenantId == harness.Other.Id);
        Assert.Equal(["Home Club", "Second Club"], listed.Memberships.Select(item => item.LegalName).ToArray());

        var selected = await harness.Service.SelectAsync(
            Staff(harness.Email, harness.AuthId, harness.Home.Id),
            harness.Second.Id,
            CancellationToken.None);

        Assert.Equal(harness.Second.Id, selected.TenantId);
        Assert.True(selected.SessionRefreshRequired);
        Assert.Equal(harness.Second.Id, harness.Auth.TenantOf(harness.AuthId));
        Assert.Equal(originalHash, harness.Auth.HashOf(harness.AuthId));
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Equal([(harness.AuthId, harness.Second.Id)], harness.Auth.MetadataUpdates);

        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Service.SelectAsync(
            Staff(harness.Email, harness.AuthId, harness.Home.Id),
            harness.Other.Id,
            CancellationToken.None));
        Assert.Equal("Tenant membership was not found.", missing.Message);
        Assert.Equal(harness.Second.Id, harness.Auth.TenantOf(harness.AuthId));
        Assert.Equal(originalHash, harness.Auth.HashOf(harness.AuthId));
    }

    [Fact]
    public async Task Inactive_membership_inactive_tenant_and_platform_admin_cannot_select()
    {
        await using var harness = await SelectionHarness.CreateAsync();
        harness.Auth.RememberCredential(harness.AuthId, "keep-this-password-1");
        harness.Auth.RegisterEmail(harness.Email, harness.AuthId, harness.Home.Id);
        var originalHash = harness.Auth.HashOf(harness.AuthId);

        var secondUser = await harness.Db.Users.IgnoreQueryFilters()
            .SingleAsync(user => user.TenantId == harness.Second.Id && user.SupabaseAuthId == harness.AuthId);
        secondUser.Deactivate();
        await harness.Db.SaveChangesAsync();

        var listed = await harness.Service.ListAsync(
            Staff(harness.Email, harness.AuthId, harness.Home.Id),
            CancellationToken.None);
        Assert.DoesNotContain(listed.Memberships, item => item.TenantId == harness.Second.Id);

        var inactiveMember = await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Service.SelectAsync(
            Staff(harness.Email, harness.AuthId, harness.Home.Id),
            harness.Second.Id,
            CancellationToken.None));
        Assert.Equal("Tenant membership was not found.", inactiveMember.Message);

        secondUser.Activate();
        harness.Home.Deactivate();
        await harness.Db.SaveChangesAsync();

        var inactiveTenant = await Assert.ThrowsAsync<KeyNotFoundException>(() => harness.Service.SelectAsync(
            Staff(harness.Email, harness.AuthId, harness.Home.Id),
            harness.Home.Id,
            CancellationToken.None));
        Assert.Equal("Tenant membership was not found.", inactiveTenant.Message);

        var admin = new StaffTenantMembershipService(
            harness.Db,
            harness.Auth,
            new FakePlatformAdminChecker(harness.Email),
            NullLogger<StaffTenantMembershipService>.Instance);
        var platform = await Assert.ThrowsAsync<InvalidOperationException>(() => admin.SelectAsync(
            Staff(harness.Email, harness.AuthId, harness.Second.Id),
            harness.Second.Id,
            CancellationToken.None));
        Assert.Equal("PLATFORM_ADMIN_USES_ENTER", platform.Message);

        Assert.Equal(harness.Home.Id, harness.Auth.TenantOf(harness.AuthId));
        Assert.Equal(originalHash, harness.Auth.HashOf(harness.AuthId));
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.MetadataUpdates);
    }

    private static ClaimsPrincipal Staff(string email, string authId, Guid tokenTenant) =>
        new(new ClaimsIdentity(
            [
                new Claim("sub", authId),
                new Claim("email", email),
                new Claim("app_metadata", $$"""{"tenant_id":"{{tokenTenant}}"}"""),
            ],
            authenticationType: "Test"));

    private sealed class SelectionHarness : IAsyncDisposable
    {
        private SelectionHarness(
            AppDbContext db,
            RecordingPasswordAuthAdmin auth,
            StaffTenantMembershipService service,
            Tenant home,
            Tenant second,
            Tenant other,
            string email,
            string authId)
        {
            Db = db;
            Auth = auth;
            Service = service;
            Home = home;
            Second = second;
            Other = other;
            Email = email;
            AuthId = authId;
        }

        public AppDbContext Db { get; }

        public RecordingPasswordAuthAdmin Auth { get; }

        public StaffTenantMembershipService Service { get; }

        public Tenant Home { get; }

        public Tenant Second { get; }

        public Tenant Other { get; }

        public string Email { get; }

        public string AuthId { get; }

        public static async Task<SelectionHarness> CreateAsync()
        {
            var home = new Tenant("Home Club", "11111111000191");
            var second = new Tenant("Second Club", "22222222000191");
            var other = new Tenant("Other Club", "33333333000191");
            var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = home.Id });
            const string email = "member@club.test";
            var authId = Guid.NewGuid().ToString();
            var otherAuthId = Guid.NewGuid().ToString();
            db.Tenants.AddRange(home, second, other);
            db.Users.Add(new User(home.Id, authId, "Member", email));
            db.Users.Add(new User(second.Id, authId, "Member", email));
            db.Users.Add(new User(other.Id, otherAuthId, "Stranger", "stranger@club.test"));
            await db.SaveChangesAsync();

            var auth = new RecordingPasswordAuthAdmin();
            var service = new StaffTenantMembershipService(
                db,
                auth,
                new FakePlatformAdminChecker(),
                NullLogger<StaffTenantMembershipService>.Instance);
            return new SelectionHarness(db, auth, service, home, second, other, email, authId);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
