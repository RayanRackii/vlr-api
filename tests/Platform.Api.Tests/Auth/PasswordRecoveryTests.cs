using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authentication;
using Platform.Api.Modules.Auth.Controllers;
using Platform.Api.Modules.Auth.Dtos;
using Platform.Api.Modules.Auth.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Auth;

public sealed class PasswordRecoveryTests
{
    private const string GenericMessage =
        "Se existir uma conta com este e-mail, enviamos um link para redefinir a senha.";

    [Fact]
    public async Task Member_of_the_current_tenant_receives_a_recovery_email_with_that_tenants_name()
    {
        var tenantA = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantB = new Tenant("Club B", "11222333000181", subdomain: "club-b");
        var tenantProvider = new FakeTenantProvider { TenantId = tenantA.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.AddRange(tenantA, tenantB);
        db.Users.Add(new User(tenantA.Id, "auth-ana", "Ana A", "ana@club.test"));
        db.Users.Add(new User(tenantB.Id, "auth-ana", "Ana B", "ana@club.test"));
        await db.SaveChangesAsync();

        var auth = new RecordingAuthAdmin { AuthUserId = "auth-ana" };
        var queue = new NotificationQueue();
        var service = CreateService(db, tenantProvider, auth, queue);

        await service.RequestAsync("ANA@club.test", CancellationToken.None);

        var message = ReadSingle(queue);
        Assert.Equal("ana@club.test", message.Recipient);
        Assert.Equal("Redefinir senha — Rolvix", message.Subject);
        Assert.Contains("Olá Ana A,", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana B", message.Body, StringComparison.Ordinal);
        Assert.Contains("token_hash=hashed-token", message.Body, StringComparison.Ordinal);
        Assert.Equal(["ana@club.test"], auth.FindCalls);
        Assert.Equal(["ana@club.test"], auth.RecoveryCalls);
    }

    [Fact]
    public async Task Email_that_exists_only_in_another_tenant_sends_nothing()
    {
        var tenantA = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantB = new Tenant("Club B", "11222333000181", subdomain: "club-b");
        var tenantProvider = new FakeTenantProvider { TenantId = tenantB.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.AddRange(tenantA, tenantB);
        db.Users.Add(new User(tenantA.Id, "auth-ana", "Ana A", "ana@club.test"));
        await db.SaveChangesAsync();

        var auth = new RecordingAuthAdmin { AuthUserId = "auth-ana" };
        var queue = new NotificationQueue();
        var service = CreateService(db, tenantProvider, auth, queue);

        await service.RequestAsync("ana@club.test", CancellationToken.None);

        Assert.False(queue.TryRead(out _));
        Assert.Empty(auth.FindCalls);
        Assert.Empty(auth.RecoveryCalls);
    }

    [Fact]
    public async Task Unknown_email_in_the_current_tenant_sends_nothing()
    {
        var tenant = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        db.Users.Add(new User(tenant.Id, "auth-ana", "Ana A", "ana@club.test"));
        await db.SaveChangesAsync();

        var auth = new RecordingAuthAdmin { AuthUserId = "auth-ana" };
        var queue = new NotificationQueue();
        var service = CreateService(db, tenantProvider, auth, queue);

        await service.RequestAsync("missing@club.test", CancellationToken.None);

        Assert.False(queue.TryRead(out _));
        Assert.Empty(auth.FindCalls);
        Assert.Empty(auth.RecoveryCalls);
    }

    [Fact]
    public async Task Missing_tenant_does_not_scan_other_tenants()
    {
        var tenantA = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenantA);
        db.Users.Add(new User(tenantA.Id, "auth-ana", "Ana A", "ana@club.test"));
        await db.SaveChangesAsync();

        var auth = new RecordingAuthAdmin { AuthUserId = "auth-ana" };
        var queue = new NotificationQueue();
        var service = CreateService(db, tenantProvider, auth, queue);

        await service.RequestAsync("ana@club.test", CancellationToken.None);

        Assert.False(queue.TryRead(out _));
        Assert.Empty(auth.FindCalls);
        Assert.Empty(auth.RecoveryCalls);
        Assert.Equal(1, await db.Users.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Member_without_an_auth_user_sends_nothing()
    {
        var tenant = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        db.Users.Add(new User(tenant.Id, "auth-ana", "Ana A", "ana@club.test"));
        await db.SaveChangesAsync();

        var auth = new RecordingAuthAdmin { AuthUserId = null };
        var queue = new NotificationQueue();
        var service = CreateService(db, tenantProvider, auth, queue);

        await service.RequestAsync("ana@club.test", CancellationToken.None);

        Assert.False(queue.TryRead(out _));
        Assert.Equal(["ana@club.test"], auth.FindCalls);
        Assert.Empty(auth.RecoveryCalls);
    }

    [Fact]
    public async Task Forgot_password_returns_the_same_message_for_member_unknown_and_other_tenant()
    {
        await using var harness = await RecoveryHarness.CreateAsync();
        var noTenant = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            null,
            CancellationToken.None);
        var member = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ANA@club.test" },
            "club-a",
            CancellationToken.None);
        var unknown = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "missing@club.test" },
            "club-a",
            CancellationToken.None);
        var otherTenant = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "only-a@club.test" },
            "club-b",
            CancellationToken.None);

        Assert.Equal(GenericMessage, MessageOf(member));
        Assert.Equal(GenericMessage, MessageOf(unknown));
        Assert.Equal(GenericMessage, MessageOf(otherTenant));
        Assert.Equal(GenericMessage, MessageOf(noTenant));

        var message = ReadSingle(harness.Queue);
        Assert.Contains("Olá Ana A,", message.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana B", message.Body, StringComparison.Ordinal);
        Assert.Equal(["ana@club.test"], harness.Auth.FindCalls);
        Assert.Equal(["ana@club.test"], harness.Auth.RecoveryCalls);
    }

    [Fact]
    public async Task Each_tenant_gets_only_its_own_display_name()
    {
        await using var harness = await RecoveryHarness.CreateAsync();

        await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);
        await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-b",
            CancellationToken.None);

        var first = ReadSingle(harness.Queue);
        var second = ReadSingle(harness.Queue);
        Assert.Contains("Olá Ana A,", first.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana B", first.Body, StringComparison.Ordinal);
        Assert.Contains("Olá Ana B,", second.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana A", second.Body, StringComparison.Ordinal);
        Assert.False(harness.Queue.TryRead(out _));
    }

    [Fact]
    public async Task Unknown_or_inactive_subdomain_returns_the_generic_message_and_sends_nothing()
    {
        await using var harness = await RecoveryHarness.CreateAsync();
        await harness.DeactivateClubBAsync();

        var unknown = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "missing-club",
            CancellationToken.None);
        var inactive = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-b",
            CancellationToken.None);

        Assert.Equal(GenericMessage, MessageOf(unknown));
        Assert.Equal(GenericMessage, MessageOf(inactive));
        Assert.Empty(harness.Auth.FindCalls);
        Assert.Empty(harness.Auth.RecoveryCalls);
        Assert.False(harness.Queue.TryRead(out _));
    }

    [Fact]
    public async Task Empty_email_is_rejected_before_any_lookup()
    {
        await using var harness = await RecoveryHarness.CreateAsync();

        var result = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "  " },
            "club-a",
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Empty(harness.Auth.FindCalls);
        Assert.False(harness.Queue.TryRead(out _));
    }

    private static PasswordRecoveryService CreateService(
        AppDbContext db,
        ITenantProvider tenantProvider,
        RecordingAuthAdmin auth,
        NotificationQueue queue) =>
        new(
            auth,
            db,
            tenantProvider,
            queue,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:FrontendBaseUrl"] = "https://rolvix.com.br",
                })
                .Build(),
            new FakeHostEnvironment { EnvironmentName = "Production" },
            NullLogger<PasswordRecoveryService>.Instance);

    private static NotificationMessage ReadSingle(NotificationQueue queue)
    {
        Assert.True(queue.TryRead(out var message));
        return message;
    }

    private static string MessageOf(ActionResult<ForgotPasswordResponseDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ForgotPasswordResponseDto>(ok.Value);
        return body.Message;
    }

    private sealed class RecoveryHarness : IAsyncDisposable
    {
        private readonly AppDbContext _db;
        private readonly Tenant _tenantB;

        private RecoveryHarness(
            AppDbContext db,
            Tenant tenantB,
            PasswordRecoveryController controller,
            NotificationQueue queue,
            RecordingAuthAdmin auth)
        {
            _db = db;
            _tenantB = tenantB;
            Controller = controller;
            Queue = queue;
            Auth = auth;
        }

        public async Task DeactivateClubBAsync()
        {
            _tenantB.Deactivate();
            await _db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => _db.DisposeAsync();

        public PasswordRecoveryController Controller { get; }

        public NotificationQueue Queue { get; }

        public RecordingAuthAdmin Auth { get; }

        public static async Task<RecoveryHarness> CreateAsync()
        {
            var tenantA = new Tenant("Club A", "66666666000191", subdomain: "club-a");
            var tenantB = new Tenant("Club B", "11222333000181", subdomain: "club-b");
            var ambient = new AmbientTenantContext();
            var tenantProvider = new HttpContextTenantProvider(
                new HttpContextAccessor(),
                ambient,
                new FakePlatformAdminChecker());
            var db = InMemoryAppDb.Create(tenantProvider);
            db.Tenants.AddRange(tenantA, tenantB);
            db.Users.Add(new User(tenantA.Id, "auth-ana", "Ana A", "ana@club.test"));
            db.Users.Add(new User(tenantA.Id, "auth-only-a", "Only A", "only-a@club.test"));
            db.Users.Add(new User(tenantB.Id, "auth-ana", "Ana B", "ana@club.test"));
            await db.SaveChangesAsync();

            var auth = new RecordingAuthAdmin { AuthUserId = "auth-ana" };
            var queue = new NotificationQueue();
            var service = CreateService(db, tenantProvider, auth, queue);
            var binder = new PublicTenantBinder(db, ambient);
            var controller = new PasswordRecoveryController(service, binder);
            return new RecoveryHarness(db, tenantB, controller, queue, auth);
        }
    }

    private sealed class RecordingAuthAdmin : ISupabaseAuthAdminClient
    {
        public string? AuthUserId { get; set; }

        public List<string> FindCalls { get; } = [];

        public List<string> RecoveryCalls { get; } = [];

        public Task<string?> FindUserIdByEmailAsync(
            string email,
            CancellationToken cancellationToken = default)
        {
            FindCalls.Add(email);
            return Task.FromResult(AuthUserId);
        }

        public Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
            string email,
            string redirectTo,
            CancellationToken cancellationToken = default)
        {
            RecoveryCalls.Add(email);
            return Task.FromResult(new SupabaseRecoveryLink("hashed-token", null));
        }

        public Task<string> CreateUserAsync(
            string email,
            string password,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UserExistsAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateUserAppMetadataAsync(
            string supabaseUserId,
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ClearUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetUserPasswordAsync(
            string supabaseUserId,
            string password,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteUserAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
