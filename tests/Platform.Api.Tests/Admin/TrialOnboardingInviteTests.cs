using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Platform.Api.Authorization;
using Platform.Api.Features.CreateTenant;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Services.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Admin;

public sealed class TrialOnboardingInviteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wizard_and_trial_share_seed_but_keep_trial_only_claim_invite_and_superadmin(bool failClosed)
    {
        await using var harness = await Harness.CreateAsync(failClosed: failClosed);
        var trialResult = await harness.Handler.HandleAsync(
            TrialRequest("seed@club.test", "11999998888"), CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Status(trialResult));
        var trialId = Body<CreateTenantResponse>(trialResult).TenantId;
        var access = new TenantAccessBootstrapper(harness.Db, NullLogger<TenantAccessBootstrapper>.Instance);
        var admin = new AdminTenantService(
            harness.Db, harness.Invites, new FakePlatformAdminMembershipService(), access,
            NullLogger<AdminTenantService>.Instance,
            new TenantProvisioner(harness.Db, NullLogger<TenantProvisioner>.Instance),
            null!); // This test never deletes a tenant.
        var wizard = await admin.CreateAsync(new CreateTenantRequestDto
        {
            LegalName = "Wizard seed", TaxId = "wizard-seed", Subdomain = "wizard-seed",
            ActiveModules = CreateTenantHandler.TrialModules,
            AssetFamilyKeys = CreateTenantHandler.TrialFamilyKeys,
        }, CancellationToken.None);

        async Task<string[]> Seed(Guid tenantId)
        {
            var db = harness.Db;
            var modules = await db.TenantModules.IgnoreQueryFilters().Where(x => x.TenantId == tenantId)
                .Select(x => "module:" + x.ModuleName + ":" + x.IsActive).ToListAsync();
            var families = await db.TenantAssetFamilies.IgnoreQueryFilters().Where(x => x.TenantId == tenantId)
                .Join(db.AssetFamilies, x => x.FamilyId, x => x.Id, (_, f) => "family:" + f.Key).ToListAsync();
            var categories = await db.AssetCategories.IgnoreQueryFilters().Where(x => x.TenantId == tenantId)
                .Select(x => "category:" + x.Name + ":" + x.Manufacturer + ":" + x.Description).ToListAsync();
            var units = await db.Units.IgnoreQueryFilters().Where(x => x.TenantId == tenantId)
                .Select(x => "unit:" + x.Name + ":" + x.Code + ":" + x.IsActive).ToListAsync();
            var policies = await db.BookingPolicies.IgnoreQueryFilters().Where(x => x.TenantId == tenantId)
                .Select(x => "policy:" + x.ResourceType + ":" + x.MaxAdvanceBookingDays).ToListAsync();
            var roles = await db.Roles.IgnoreQueryFilters().Include(x => x.RolePermissions)
                .ThenInclude(x => x.Permission).Where(x => x.TenantId == tenantId).ToListAsync();
            var accessSeed = roles.Where(x => x.Name != SystemRoles.SuperAdmin).SelectMany(x =>
                new[] { "role:" + x.Name + ":" + x.IsSystemRole }.Concat(
                    x.RolePermissions.Select(p => "permission:" + x.Name + ":" + p.Permission.Key)));
            return modules.Concat(families).Concat(categories).Concat(units).Concat(policies)
                .Concat(accessSeed).Order(StringComparer.Ordinal).ToArray();
        }

        var trialSeed = await Seed(trialId);
        Assert.NotEmpty(trialSeed);
        Assert.Equal(trialSeed, await Seed(wizard.Id));
        Assert.Contains("policy::7", trialSeed);
        Assert.Contains("unit:Matriz:HQ:True", trialSeed);
        Assert.Contains(trialSeed, x => x.StartsWith("permission:"));
        Assert.Contains(trialSeed, x => x.StartsWith("category:"));
        var trial = await harness.Db.Tenants.SingleAsync(x => x.Id == trialId);
        Assert.True(trial.IsTrial);
        Assert.NotNull(trial.TrialEndsAt);
        Assert.NotNull(trial.TrialPurgeAt);
        Assert.False((await harness.Db.Tenants.SingleAsync(x => x.Id == wizard.Id)).IsTrial);
        Assert.Equal(trialId, (await harness.Db.TrialSignupClaims.SingleAsync()).TenantId);
        Assert.Equal(trialId, (await harness.Db.UserInvites.IgnoreQueryFilters().SingleAsync()).TenantId);
        var superAdmin = await harness.Db.Roles.IgnoreQueryFilters().Include(x => x.RolePermissions)
            .SingleAsync(x => x.TenantId == trialId && x.Name == SystemRoles.SuperAdmin);
        Assert.NotEmpty(superAdmin.RolePermissions);
        Assert.False(await harness.Db.Roles.IgnoreQueryFilters()
            .AnyAsync(x => x.TenantId == wizard.Id && x.Name == SystemRoles.SuperAdmin));
    }

    [Fact]
    public void Trial_json_without_admin_password_binds_as_omitted()
    {
        var json = """
            {
              "legalName": "Club",
              "taxId": "1",
              "headquartersUnitName": "Matriz",
              "adminFullName": "Ada Admin",
              "adminEmail": "ada@club.test",
              "adminPhone": "11999998888",
              "isTrial": true
            }
            """;

        var request = JsonSerializer.Deserialize<CreateTenantRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Null(request.AdminPassword);
        Assert.True(request.IsTrial);
        Assert.Equal("11999998888", request.AdminPhone);
    }

    [Fact]
    public async Task Trial_without_password_creates_pending_invite_and_skips_auth_user()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Handler.HandleAsync(
            TrialRequest("Ada@Club.test", "11999998888"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
        var body = Body<CreateTenantResponse>(result);
        var horizon = await harness.Db.BookingPolicies
            .IgnoreQueryFilters()
            .SingleAsync(item => item.TenantId == body.TenantId);
        Assert.Null(horizon.ResourceType);
        Assert.Equal(7, horizon.MaxAdvanceBookingDays);
        Assert.Equal(7, BookingPolicy.SuggestedMaxAdvanceBookingDays);
        Assert.Null(body.AdminUserId);
        Assert.Null(body.SupabaseAuthId);
        Assert.NotNull(body.InviteId);
        Assert.NotEqual(Guid.Empty, body.SuperAdminRoleId);

        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.MetadataUpdates);
        Assert.Empty(await harness.Db.Users.ToListAsync());

        var invite = await harness.Db.UserInvites
            .Include(item => item.InviteRoles)
            .SingleAsync();
        Assert.Equal(body.InviteId, invite.Id);
        Assert.Equal(body.TenantId, invite.TenantId);
        Assert.Equal("ada@club.test", invite.Email);
        Assert.Equal(SystemRoles.SuperAdmin, invite.RoleName);
        Assert.True(invite.IsPending);
        Assert.Contains(invite.InviteRoles, item => item.RoleId == body.SuperAdminRoleId);

        var role = await harness.Db.Roles.SingleAsync(item => item.Id == body.SuperAdminRoleId);
        Assert.Equal(SystemRoles.SuperAdmin, role.Name);
        Assert.True(role.IsSystemRole);

        Assert.True(harness.Queue.TryRead(out var message));
        Assert.False(harness.Queue.TryRead(out _));
        Assert.Equal("Email", message.Type);
        Assert.Equal("ada@club.test", message.Recipient);
        Assert.StartsWith("Convite Rolvix — admin de ", message.Subject, StringComparison.Ordinal);
        Assert.Contains("para administrar", message.Body, StringComparison.Ordinal);
        Assert.Contains("no console Rolvix", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("como <strong>", message.Body, StringComparison.Ordinal);
        Assert.Contains($"/invite?token={invite.Token}", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(invite.Token, string.Join('\n', harness.Logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trial_with_blank_password_is_treated_as_omitted()
    {
        await using var harness = await Harness.CreateAsync();

        var result = await harness.Handler.HandleAsync(
            TrialRequest("blank@club.test", "11999997777", password: "   "),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
        Assert.Empty(harness.Auth.Created);
        Assert.NotNull(Body<CreateTenantResponse>(result).InviteId);
    }

    [Fact]
    public async Task Trial_with_admin_password_is_rejected()
    {
        await using var harness = await Harness.CreateAsync();
        const string suppliedPassword = "SuppliedSecret99";

        var result = await harness.Handler.HandleAsync(
            TrialRequest("secret@club.test", "11999996666", password: suppliedPassword),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        Assert.Equal("AdminPassword is not accepted.", Error(result));
        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(await harness.Db.Tenants.ToListAsync());
        Assert.Empty(await harness.Db.TrialSignupClaims.ToListAsync());
        Assert.Empty(await harness.Db.UserInvites.ToListAsync());
        Assert.False(harness.Queue.TryRead(out _));
        Assert.DoesNotContain(
            suppliedPassword,
            string.Join('\n', harness.Logger.Messages),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trial_provisions_tenant_and_invite_email_when_the_tenant_filter_is_fail_closed()
    {
        // Anonymous trial has no tenant: under Tenancy:FailClosed only a PlatformScope lets it read.
        await using var harness = await Harness.CreateAsync(failClosed: true);

        var result = await harness.Handler.HandleAsync(
            TrialRequest("Ada@Club.test", "11999998888"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, Status(result));
        var body = Body<CreateTenantResponse>(result);
        var invite = await harness.Db.UserInvites.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(body.InviteId, invite.Id);
        Assert.True(harness.Queue.TryRead(out var message));
        Assert.Equal("ada@club.test", message.Recipient);

        // Super-Admin in platform mode (no tenant) manages the new tenant's invites.
        var bundle = await harness.Invites.ListUsersAndInvitesAsync(body.TenantId, CancellationToken.None);
        Assert.Equal([invite.Id], bundle.Invites.Select(item => item.Id));
        await harness.Invites.RevokeInviteAsync(body.TenantId, invite.Id, CancellationToken.None);
    }

    [Fact]
    public async Task Duplicate_trial_email_returns_409()
    {
        await using var harness = await Harness.CreateAsync();

        var first = await harness.Handler.HandleAsync(
            TrialRequest("Same@Club.test", "11911112222", legalName: "First Club"),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Status(first));

        var second = await harness.Handler.HandleAsync(
            TrialRequest("same@club.test", "11933334444", legalName: "Second Club"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, Status(second));
        Assert.Equal("Trial already used for this email or phone.", Error(second));
        Assert.Equal(1, await harness.Db.Tenants.CountAsync());
        Assert.Equal(1, await harness.Db.UserInvites.CountAsync());
        Assert.Empty(harness.Auth.Created);
    }

    [Fact]
    public async Task Duplicate_trial_phone_returns_409()
    {
        await using var harness = await Harness.CreateAsync();

        var first = await harness.Handler.HandleAsync(
            TrialRequest("phone-a@club.test", "11988887777", legalName: "Phone Club A"),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Status(first));

        var second = await harness.Handler.HandleAsync(
            TrialRequest("phone-b@club.test", "(11) 98888-7777", legalName: "Phone Club B"),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, Status(second));
        Assert.Equal("Trial already used for this email or phone.", Error(second));
        Assert.Equal(1, await harness.Db.Tenants.CountAsync());
        Assert.Empty(harness.Auth.Created);
    }

    [Fact]
    public async Task Accept_invite_sets_the_password_chosen_by_the_invitee()
    {
        await using var harness = await Harness.CreateAsync();
        const string chosenPassword = "invitee-owns-1";

        var created = await harness.Handler.HandleAsync(
            TrialRequest("join@club.test", "11922223333", legalName: "Join Club"),
            CancellationToken.None);
        var body = Body<CreateTenantResponse>(created);
        var invite = await harness.Db.UserInvites.SingleAsync();
        Assert.Empty(harness.Auth.Created);

        var accepted = await harness.Invites.AcceptInviteAsync(
            new AcceptInviteRequestDto
            {
                Token = invite.Token,
                Password = chosenPassword,
            },
            CancellationToken.None);

        var createdAuth = Assert.Single(harness.Auth.Created);
        Assert.Equal("join@club.test", createdAuth.Email);
        Assert.Equal(chosenPassword, createdAuth.Password);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Contains(harness.Auth.MetadataUpdates, item => item.TenantId == body.TenantId);

        var user = await harness.Db.Users.SingleAsync();
        Assert.Equal(accepted.UserId, user.Id);
        Assert.Equal(createdAuth.UserId, user.SupabaseAuthId);
        Assert.Equal("join@club.test", user.Email);

        var assignment = await harness.Db.UserRoles.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(body.SuperAdminRoleId, assignment.RoleId);

        var storedInvite = await harness.Db.UserInvites.SingleAsync();
        Assert.NotNull(storedInvite.AcceptedAt);
        Assert.Equal(user.Id, storedInvite.CreatedUserId);

        Assert.DoesNotContain(
            chosenPassword,
            string.Join('\n', harness.Logger.Messages),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            invite.Token,
            string.Join('\n', harness.Logger.Messages),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wizard_invite_still_sends_the_same_email_without_a_password()
    {
        await using var harness = await Harness.CreateAsync();
        var tenant = new Tenant("Wizard Club", "99888777000191");
        harness.Db.Tenants.Add(tenant);
        await harness.Db.SaveChangesAsync();

        var invite = await harness.Invites.InviteAsync(
            tenant.Id,
            new InviteTenantUserRequestDto
            {
                FullName = "Wizard Admin",
                Email = "Wizard@Club.test",
                RoleName = SystemRoles.Admin,
            },
            CancellationToken.None);

        Assert.Equal(SystemRoles.Admin, invite.RoleName);
        Assert.Empty(harness.Auth.Created);

        var stored = await harness.Db.UserInvites.SingleAsync();
        Assert.True(harness.Queue.TryRead(out var message));
        Assert.Equal("wizard@club.test", message.Recipient);
        Assert.Contains($"/invite?token={stored.Token}", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Token, string.Join('\n', harness.Logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_without_is_trial_binds_as_false()
    {
        var json = """
            {
              "legalName": "Club",
              "taxId": "1",
              "headquartersUnitName": "Matriz",
              "adminFullName": "Ada Admin",
              "adminEmail": "ada@club.test",
              "adminPhone": "11999998888",
              "adminPassword": "password1"
            }
            """;

        var request = JsonSerializer.Deserialize<CreateTenantRequest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.False(request.IsTrial);
        Assert.Equal("password1", request.AdminPassword);
    }

    [Fact]
    public async Task Anonymous_non_trial_is_rejected_and_creates_nothing()
    {
        await using var harness = await Harness.CreateAsync();
        const string suppliedPassword = "attacker-chosen-1";
        const string clientIp = "203.0.113.44";

        var explicitFalse = await harness.Handler.HandleAsync(
            new CreateTenantRequest(
                "Legacy Club",
                "11222333000181",
                null,
                "Matriz",
                "HQ",
                "Legacy Admin",
                "legacy@club.test",
                suppliedPassword,
                "11977776666",
                IsTrial: false),
            CancellationToken.None,
            clientIp);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(explicitFalse));
        Assert.Equal(AnonymousOnboardingErrorCodes.TrialRequired, Error(explicitFalse));

        var omitted = await harness.Handler.HandleAsync(
            new CreateTenantRequest(
                "Legacy Club",
                "11222333000181",
                null,
                "Matriz",
                "HQ",
                "Legacy Admin",
                "legacy@club.test",
                suppliedPassword,
                "11977776666"),
            CancellationToken.None,
            clientIp);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(omitted));
        Assert.Equal(AnonymousOnboardingErrorCodes.TrialRequired, Error(omitted));
        Assert.Empty(harness.Auth.Created);
        Assert.Empty(harness.Auth.PasswordsSet);
        Assert.Empty(harness.Auth.MetadataUpdates);
        Assert.Empty(await harness.Db.Tenants.ToListAsync());
        Assert.Empty(await harness.Db.TrialSignupClaims.ToListAsync());
        Assert.Empty(await harness.Db.UserInvites.ToListAsync());
        Assert.Empty(await harness.Db.Users.ToListAsync());
        Assert.False(harness.Queue.TryRead(out _));
        var logs = string.Join('\n', harness.Logger.Messages.Concat(harness.HandlerLogger.Messages));
        Assert.DoesNotContain(suppliedPassword, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_non_trial_does_not_consume_the_ip_budget()
    {
        await using var harness = await Harness.CreateAsync();
        const string clientIp = "203.0.113.45";

        for (var i = 0; i < AnonymousTrialCreateGate.IpMaxAttempts + 1; i++)
        {
            var rejected = await harness.Handler.HandleAsync(
                new CreateTenantRequest(
                    "Legacy Club",
                    "11222333000181",
                    null,
                    "Matriz",
                    "HQ",
                    "Legacy Admin",
                    $"legacy{i}@club.test",
                    "password1",
                    IsTrial: false),
                CancellationToken.None,
                clientIp);
            Assert.Equal(AnonymousOnboardingErrorCodes.TrialRequired, Error(rejected));
        }

        var created = await harness.Handler.HandleAsync(
            TrialRequest("after-reject@club.test", "11966665555"),
            CancellationToken.None,
            clientIp);

        Assert.Equal(StatusCodes.Status201Created, Status(created));
        Assert.Equal(1, await harness.Db.Tenants.CountAsync());
        Assert.Empty(harness.Auth.Created);
    }

    [Fact]
    public async Task Ip_limit_blocks_the_next_trial_and_creates_nothing_more()
    {
        await using var harness = await Harness.CreateAsync();
        const string clientIp = "203.0.113.10";

        for (var i = 0; i < AnonymousTrialCreateGate.IpMaxAttempts; i++)
        {
            var created = await harness.Handler.HandleAsync(
                TrialRequest($"rate{i}@club.test", $"1191000{i:0000}"),
                CancellationToken.None,
                clientIp);
            Assert.Equal(StatusCodes.Status201Created, Status(created));
        }

        var blocked = await harness.Handler.HandleAsync(
            TrialRequest("blocked@club.test", "11910009999"),
            CancellationToken.None,
            clientIp);

        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(blocked));
        Assert.Equal(AnonymousOnboardingErrorCodes.RateLimited, Error(blocked));
        Assert.Equal(AnonymousTrialCreateGate.IpMaxAttempts, await harness.Db.Tenants.CountAsync());
        Assert.Equal(AnonymousTrialCreateGate.IpMaxAttempts, await harness.Db.UserInvites.CountAsync());
        Assert.Empty(harness.Auth.Created);

        var recipients = DrainRecipients(harness);
        Assert.Equal(AnonymousTrialCreateGate.IpMaxAttempts, recipients.Count);
        Assert.DoesNotContain("blocked@club.test", recipients);
    }

    [Fact]
    public async Task A_different_ip_is_not_blocked_by_another_address()
    {
        await using var harness = await Harness.CreateAsync();

        for (var i = 0; i < AnonymousTrialCreateGate.IpMaxAttempts; i++)
        {
            var created = await harness.Handler.HandleAsync(
                TrialRequest($"ip-a{i}@club.test", $"1192000{i:0000}"),
                CancellationToken.None,
                "203.0.113.10");
            Assert.Equal(StatusCodes.Status201Created, Status(created));
        }

        var blocked = await harness.Handler.HandleAsync(
            TrialRequest("ip-a-blocked@club.test", "11920009999"),
            CancellationToken.None,
            "203.0.113.10");
        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(blocked));

        var other = await harness.Handler.HandleAsync(
            TrialRequest("ip-b@club.test", "11920008888"),
            CancellationToken.None,
            "203.0.113.11");
        Assert.Equal(StatusCodes.Status201Created, Status(other));
        Assert.Equal(AnonymousTrialCreateGate.IpMaxAttempts + 1, await harness.Db.Tenants.CountAsync());
    }

    [Fact]
    public async Task Missing_and_blank_ip_share_one_bucket()
    {
        await using var harness = await Harness.CreateAsync();

        for (var i = 0; i < AnonymousTrialCreateGate.IpMaxAttempts; i++)
        {
            var created = await harness.Handler.HandleAsync(
                TrialRequest($"anon{i}@club.test", $"1193000{i:0000}"),
                CancellationToken.None,
                clientIp: null);
            Assert.Equal(StatusCodes.Status201Created, Status(created));
        }

        var blocked = await harness.Handler.HandleAsync(
            TrialRequest("anon-blocked@club.test", "11930009999"),
            CancellationToken.None,
            clientIp: " ");
        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(blocked));
        Assert.Equal(AnonymousOnboardingErrorCodes.RateLimited, Error(blocked));
        Assert.Equal(AnonymousTrialCreateGate.IpMaxAttempts, await harness.Db.Tenants.CountAsync());
    }

    [Fact]
    public async Task Ip_limit_expires_after_the_window()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var gate = new AnonymousTrialCreateGate(new MemoryCache(new MemoryCacheOptions()), time);
        await using var harness = await Harness.CreateAsync(gate);

        for (var i = 0; i < AnonymousTrialCreateGate.IpMaxAttempts; i++)
        {
            var created = await harness.Handler.HandleAsync(
                TrialRequest($"window{i}@club.test", $"1194000{i:0000}"),
                CancellationToken.None,
                "203.0.113.12");
            Assert.Equal(StatusCodes.Status201Created, Status(created));
        }

        var blocked = await harness.Handler.HandleAsync(
            TrialRequest("window-blocked@club.test", "11940009999"),
            CancellationToken.None,
            "203.0.113.12");
        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(blocked));

        time.Advance(AnonymousTrialCreateGate.IpWindow);

        var allowed = await harness.Handler.HandleAsync(
            TrialRequest("window-later@club.test", "11940008888"),
            CancellationToken.None,
            "203.0.113.12");
        Assert.Equal(StatusCodes.Status201Created, Status(allowed));
    }

    [Theory]
    [InlineData(409, StatusCodes.Status409Conflict, AnonymousOnboardingErrorCodes.SignupRejected)]
    [InlineData(422, StatusCodes.Status409Conflict, AnonymousOnboardingErrorCodes.SignupRejected)]
    [InlineData(500, StatusCodes.Status502BadGateway, AnonymousOnboardingErrorCodes.SignupUnavailable)]
    public async Task Auth_provider_failures_do_not_echo_the_provider_message(
        int providerStatus,
        int httpStatus,
        string errorCode)
    {
        const string raw = "supabase raw detail user already registered victim@secret.test";
        var invites = new ThrowingInviteService(
            new SupabaseAuthAdminException(raw, providerStatus));
        await using var harness = await Harness.CreateAsync(invites: invites);

        var result = await harness.Handler.HandleAsync(
            TrialRequest($"provider{providerStatus}@club.test", "11955554444"),
            CancellationToken.None,
            "203.0.113.50");

        Assert.Equal(httpStatus, Status(result));
        Assert.Equal(errorCode, Error(result));
        Assert.DoesNotContain("supabase raw", Error(result), StringComparison.Ordinal);
        Assert.DoesNotContain("victim@secret.test", Error(result), StringComparison.Ordinal);
        Assert.Contains(raw, string.Join('\n', harness.HandlerLogger.Messages), StringComparison.Ordinal);
        Assert.False(harness.Queue.TryRead(out _));
        Assert.Empty(harness.Auth.Created);
    }

    private static CreateTenantRequest TrialRequest(
        string email,
        string phone,
        string? password = null,
        string? legalName = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new CreateTenantRequest(
            legalName ?? $"Club {suffix}",
            $"99{suffix}000191",
            null,
            "Matriz",
            "HQ",
            "Ada Admin",
            email,
            password,
            phone,
            IsTrial: true);
    }

    private static int Status(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode
        ?? throw new InvalidOperationException("Result has no status code.");

    private static T Body<T>(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        return Assert.IsType<T>(value);
    }

    private static string Error(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value
            ?? throw new InvalidOperationException("Result has no body.");
        var error = value.GetType().GetProperty("error")?.GetValue(value);
        return Assert.IsType<string>(error);
    }

    private static List<string> DrainRecipients(Harness harness)
    {
        var recipients = new List<string>();
        while (harness.Queue.TryRead(out var message))
        {
            recipients.Add(message.Recipient);
        }

        return recipients;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            AppDbContext db,
            CreateTenantHandler handler,
            ITenantUserAdminService invites,
            RecordingAuth auth,
            NotificationQueue queue,
            ListLogger<TenantUserAdminService> logger,
            ListLogger<CreateTenantHandler> handlerLogger)
        {
            Db = db;
            Handler = handler;
            Invites = invites;
            Auth = auth;
            Queue = queue;
            Logger = logger;
            HandlerLogger = handlerLogger;
        }

        public AppDbContext Db { get; }

        public CreateTenantHandler Handler { get; }

        public ITenantUserAdminService Invites { get; }

        public RecordingAuth Auth { get; }

        public NotificationQueue Queue { get; }

        public ListLogger<TenantUserAdminService> Logger { get; }

        public ListLogger<CreateTenantHandler> HandlerLogger { get; }

        public static async Task<Harness> CreateAsync(
            IAnonymousTrialCreateGate? trialCreateGate = null,
            ITenantUserAdminService? invites = null,
            bool failClosed = false)
        {
            var tenantProvider = new FakeTenantProvider { TenantId = null };
            var db = InMemoryAppDb.Create(tenantProvider, ignoreInMemoryTransactions: true, failClosed: failClosed);
            db.AssetFamilies.AddRange(
                Family(AssetFamilyKeys.Spaces, "Spaces", 1),
                Family(AssetFamilyKeys.Electrical, "Electrical", 2),
                Family(AssetFamilyKeys.Goods, "Goods", 3),
                Family(AssetFamilyKeys.Generic, "Generic", 4));
            await db.SaveChangesAsync();

            var auth = new RecordingAuth();
            var queue = new NotificationQueue();
            var logger = new ListLogger<TenantUserAdminService>();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:FrontendBaseUrl"] = "https://app.example.test",
                })
                .Build();
            var platformAdmin = new FakePlatformAdminChecker();
            var inviteService = invites ?? new TenantUserAdminService(
                db,
                auth,
                queue,
                configuration,
                new FakeHostEnvironment(),
                new TrialGuard(db, tenantProvider, platformAdmin),
                platformAdmin,
                logger);
            var handlerLogger = new ListLogger<CreateTenantHandler>();
            var handler = new CreateTenantHandler(
                db,
                new TenantProvisioner(db, NullLogger<TenantProvisioner>.Instance),
                new TenantAccessBootstrapper(
                    db,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantAccessBootstrapper>.Instance),
                inviteService,
                trialCreateGate ?? new AnonymousTrialCreateGate(
                    new MemoryCache(new MemoryCacheOptions()),
                    TimeProvider.System),
                handlerLogger);

            return new Harness(db, handler, inviteService, auth, queue, logger, handlerLogger);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static AssetFamily Family(string key, string label, int sortOrder) =>
            new()
            {
                Key = key,
                Label = label,
                FieldSchemaJson = """{"fields":[]}""",
                SortOrder = sortOrder,
                IsActive = true,
            };
    }

    private sealed class RecordingAuth : ISupabaseAuthAdminClient
    {
        public List<CreatedAuthUser> Created { get; } = [];

        public List<(string UserId, Guid TenantId)> MetadataUpdates { get; } = [];

        public List<(string UserId, string Password)> PasswordsSet { get; } = [];

        public string NextUserId { get; set; } = "auth-user-1";

        public Task<string> CreateUserAsync(
            string email,
            string password,
            CancellationToken cancellationToken = default)
        {
            Created.Add(new CreatedAuthUser(email, password, NextUserId));
            return Task.FromResult(NextUserId);
        }

        public Task<string?> FindUserIdByEmailAsync(
            string email,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> UserExistsAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task UpdateUserAppMetadataAsync(
            string supabaseUserId,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            MetadataUpdates.Add((supabaseUserId, tenantId));
            return Task.CompletedTask;
        }

        public Task ClearUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Guid?> GetUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

        public Task SetUserPasswordAsync(
            string supabaseUserId,
            string password,
            CancellationToken cancellationToken = default)
        {
            PasswordsSet.Add((supabaseUserId, password));
            return Task.CompletedTask;
        }

        public Task DeleteUserAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
            string email,
            string redirectTo,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SupabaseRecoveryLink("token", null));
    }

    private sealed record CreatedAuthUser(string Email, string Password, string UserId);

    private sealed class ThrowingInviteService(SupabaseAuthAdminException exception) : ITenantUserAdminService
    {
        public Task<TenantUsersBundleDto> ListUsersAndInvitesAsync(
            Guid tenantId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantInviteResponseDto> InviteAsync(
            Guid tenantId,
            InviteTenantUserRequestDto request,
            CancellationToken cancellationToken,
            bool enqueueEmail = true) =>
            throw exception;

        public Task DeliverInviteEmailAsync(
            Guid tenantId,
            Guid inviteId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ResendInviteAsync(
            Guid tenantId,
            Guid inviteId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RevokeInviteAsync(
            Guid tenantId,
            Guid inviteId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task PromoteAsync(
            Guid tenantId,
            Guid userId,
            PromoteTenantUserRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InvitePreviewResponseDto> PreviewInviteAsync(
            string token,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AcceptInviteResponseDto> AcceptInviteAsync(
            AcceptInviteRequestDto request,
            CancellationToken cancellationToken,
            System.Security.Claims.ClaimsPrincipal? actor = null) =>
            throw new NotSupportedException();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (exception is not null)
            {
                text = $"{text} {exception}";
            }

            Messages.Add(text);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
