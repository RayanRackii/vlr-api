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

namespace Platform.Api.Tests.Notifications;

public sealed class StaffInviteEmailTests
{
    [Fact]
    public void Professor_wording_names_the_role_and_does_not_say_administrar()
    {
        var html = RolvixEmailLayout.InviteBody(
            "https://rolvix.com.br/invite?token=abc",
            "Estúdio Norte",
            roleName: "Professor");

        Assert.Contains(
            "Você foi convidado(a) para acessar <strong>Estúdio Norte</strong> como <strong>Professor</strong>.",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("administrar", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Quadra", html, StringComparison.Ordinal);
        Assert.DoesNotContain("FICC", html, StringComparison.Ordinal);
        Assert.Equal(
            "Convite Rolvix — Estúdio Norte",
            RolvixEmailLayout.InviteSubject("Estúdio Norte", ["Professor"]));
    }

    [Theory]
    [InlineData(SystemRoles.Admin)]
    [InlineData(SystemRoles.SuperAdmin)]
    [InlineData("admin")]
    public void Administrative_roles_keep_the_admin_sentence(string roleName)
    {
        var html = RolvixEmailLayout.InviteBody(
            "https://rolvix.com.br/invite?token=abc",
            "Estúdio Norte",
            roleName: roleName);

        Assert.Contains(
            "Você foi convidado(a) para administrar <strong>Estúdio Norte</strong> no console Rolvix.",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("como <strong>", html, StringComparison.Ordinal);
        Assert.Equal(
            "Convite Rolvix — admin de Estúdio Norte",
            RolvixEmailLayout.InviteSubject("Estúdio Norte", [roleName]));
    }

    [Fact]
    public void Mixed_roles_use_access_wording_for_every_assigned_name()
    {
        var html = RolvixEmailLayout.InviteBody(
            "https://rolvix.com.br/invite?token=abc",
            "Estúdio Norte",
            roleNames: [SystemRoles.Admin, "Professor"]);

        Assert.Contains("para acessar <strong>Estúdio Norte</strong> como <strong>", html, StringComparison.Ordinal);
        Assert.Contains("Admin", html, StringComparison.Ordinal);
        Assert.Contains("Professor", html, StringComparison.Ordinal);
        Assert.DoesNotContain("para administrar", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Invite_subject_strips_line_breaks_and_unicode_separators()
    {
        const string hostile = "Acme\r\nBcc: evil@x.com\0\u2028\u2029\u0085";

        Assert.Equal(
            "Convite Rolvix — AcmeBcc: evil@x.com",
            RolvixEmailLayout.InviteSubject(hostile, ["Professor"]));
        Assert.Equal(
            "Convite Rolvix — admin de AcmeBcc: evil@x.com",
            RolvixEmailLayout.InviteSubject(hostile, [SystemRoles.Admin]));
        Assert.Equal(
            "Convite Rolvix — sua empresa",
            RolvixEmailLayout.InviteSubject("\r\n\0\u2028\u2029\u0085", ["Professor"]));

        var subject = RolvixEmailLayout.InviteSubject(hostile, ["Professor"]);
        Assert.DoesNotContain("\r", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2028", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", subject, StringComparison.Ordinal);
        Assert.DoesNotContain("\u0085", subject, StringComparison.Ordinal);
    }

    [Fact]
    public void Role_name_is_encoded()
    {
        var html = RolvixEmailLayout.InviteBody(
            "https://rolvix.com.br/invite?token=abc",
            "Estúdio Norte",
            roleName: "Professor <script>");

        Assert.Contains("Professor &lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Technician_invite_from_the_admin_service_names_the_role()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Service.InviteAsync(
            fx.TenantId,
            new InviteTenantUserRequestDto
            {
                FullName = "Ada Technician",
                Email = "ada@estudio.test",
                RoleName = SystemRoles.Technician,
            },
            CancellationToken.None);

        Assert.True(fx.Queue.TryRead(out var message));
        Assert.Contains(
            "como <strong>Technician</strong>",
            message.Body,
            StringComparison.Ordinal);
        Assert.Contains("Estúdio Norte", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("administrar", message.Body, StringComparison.Ordinal);
        Assert.Equal("Convite Rolvix — Estúdio Norte", message.Subject);
        Assert.False(fx.Queue.TryRead(out _));
    }

    [Fact]
    public async Task Admin_invite_from_the_admin_service_keeps_admin_wording()
    {
        await using var fx = await Fixture.CreateAsync();
        await fx.Service.InviteAsync(
            fx.TenantId,
            new InviteTenantUserRequestDto
            {
                FullName = "Ada Admin",
                Email = "ada-admin@estudio.test",
                RoleName = SystemRoles.Admin,
            },
            CancellationToken.None);

        Assert.True(fx.Queue.TryRead(out var message));
        Assert.Contains(
            "para administrar <strong>Estúdio Norte</strong> no console Rolvix.",
            message.Body,
            StringComparison.Ordinal);
        Assert.Equal("Convite Rolvix — admin de Estúdio Norte", message.Subject);
        Assert.DoesNotContain("como <strong>", message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Role_from_another_tenant_is_not_copied_into_the_invite_wording()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenantA = new Tenant("Estúdio Norte", "40404040000191");
        var tenantB = new Tenant("Outro Lugar", "50505050000191");
        var foreignRole = new Role(tenantB.Id, "Outro");
        var invite = new UserInvite(
            tenantA.Id,
            "ada@estudio.test",
            "Ada",
            "Professor",
            "token",
            DateTimeOffset.UtcNow.AddDays(1));
        db.Tenants.AddRange(tenantA, tenantB);
        db.Roles.Add(foreignRole);
        db.UserInvites.Add(invite);
        db.UserInviteRoles.Add(new UserInviteRole(invite.Id, foreignRole.Id));
        await db.SaveChangesAsync();
        tenantProvider.TenantId = null;

        var names = await StaffInviteRoleNames.LoadAsync(
            db,
            tenantA.Id,
            invite.Id,
            invite.RoleName,
            CancellationToken.None);

        Assert.Equal(["Professor"], names);
        Assert.DoesNotContain("Outro", names);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(AppDbContext db, TenantUserAdminService service, NotificationQueue queue, Guid tenantId)
        {
            Db = db;
            Service = service;
            Queue = queue;
            TenantId = tenantId;
        }

        public AppDbContext Db { get; }

        public TenantUserAdminService Service { get; }

        public NotificationQueue Queue { get; }

        public Guid TenantId { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var tenantProvider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(tenantProvider);
            var tenant = new Tenant("Estúdio Norte", "60606060000191", subdomain: "estudio");
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            tenantProvider.TenantId = tenant.Id;
            var queue = new NotificationQueue();
            var service = new TenantUserAdminService(
                db,
                new FakeSupabaseAuthAdminClient(),
                queue,
                new ConfigurationBuilder().Build(),
                new FakeHostEnvironment(),
                new FakeTrialGuard(),
                new FakePlatformAdminChecker(),
                NullLogger<TenantUserAdminService>.Instance);
            return new Fixture(db, service, queue, tenant.Id);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }
}
