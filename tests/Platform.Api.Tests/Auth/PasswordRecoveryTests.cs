using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authentication;
using Platform.Api.Modules.Auth.Controllers;
using Platform.Api.Modules.Auth.Dtos;
using Platform.Api.Modules.Auth.Services;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
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
    public async Task Member_without_an_auth_user_still_starts_the_cooldown()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var gate = new PhoneVerificationSendGate(new MemoryCache(new MemoryCacheOptions()), time);
        await using var harness = await RecoveryHarness.CreateAsync(gate);
        harness.Auth.AuthUserId = null;

        var first = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);
        var second = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);

        Assert.Equal(GenericMessage, MessageOf(first));
        Assert.Equal(GenericMessage, MessageOf(second));
        Assert.False(harness.Queue.TryRead(out _));
        Assert.Equal(["ana@club.test"], harness.Auth.FindCalls);
        Assert.Empty(harness.Auth.RecoveryCalls);
    }

    [Fact]
    public async Task Canceled_lookup_propagates()
    {
        var tenant = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        db.Users.Add(new User(tenant.Id, "auth-ana", "Ana A", "ana@club.test"));
        await db.SaveChangesAsync();

        var auth = new RecordingAuthAdmin { AuthUserId = "auth-ana" };
        var service = CreateService(db, tenantProvider, auth, new NotificationQueue());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RequestAsync("ana@club.test", canceled.Token));
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

    [Fact]
    public async Task Deactivated_member_gets_the_generic_message_and_no_recovery_email()
    {
        await using var harness = await RecoveryHarness.CreateAsync();
        await harness.DeactivateAnaAAsync();

        var result = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);

        Assert.Equal(GenericMessage, MessageOf(result));
        Assert.False(harness.Queue.TryRead(out _));
        Assert.Empty(harness.Auth.FindCalls);
        Assert.Empty(harness.Auth.RecoveryCalls);
    }

    [Fact]
    public async Task Cooldown_returns_the_generic_message_and_does_not_queue_a_second_email()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-10-14T12:00:00Z"));
        var gate = new PhoneVerificationSendGate(new MemoryCache(new MemoryCacheOptions()), time);
        await using var harness = await RecoveryHarness.CreateAsync(gate);

        var first = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);
        var second = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ANA@club.test" },
            "club-a",
            CancellationToken.None);

        Assert.Equal(GenericMessage, MessageOf(first));
        Assert.Equal(GenericMessage, MessageOf(second));
        Assert.Contains("Olá Ana A,", ReadSingle(harness.Queue).Body, StringComparison.Ordinal);
        Assert.False(harness.Queue.TryRead(out _));
        Assert.Equal(["ana@club.test"], harness.Auth.RecoveryCalls);

        var otherTenant = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-b",
            CancellationToken.None);
        Assert.Equal(GenericMessage, MessageOf(otherTenant));
        Assert.Contains("Olá Ana B,", ReadSingle(harness.Queue).Body, StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(46));
        var afterCooldown = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);
        Assert.Equal(GenericMessage, MessageOf(afterCooldown));
        Assert.Contains("Olá Ana A,", ReadSingle(harness.Queue).Body, StringComparison.Ordinal);
        Assert.Equal(3, harness.Auth.RecoveryCalls.Count);
    }

    [Fact]
    public async Task Ip_limit_returns_429_and_customer_reset_limit_does_not_block_staff_recovery()
    {
        var openCache = new MemoryCache(new MemoryCacheOptions());
        var openGate = new PhoneVerificationSendGate(openCache, TimeProvider.System);
        await using var open = await RecoveryHarness.CreateAsync(openGate);
        FillIpWindow(openGate, IPhoneVerificationSendGate.PasswordResetScope);

        var allowed = await open.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);

        Assert.Equal(GenericMessage, MessageOf(allowed));
        Assert.Contains("Olá Ana A,", ReadSingle(open.Queue).Body, StringComparison.Ordinal);

        var limitedCache = new MemoryCache(new MemoryCacheOptions());
        var limitedGate = new PhoneVerificationSendGate(limitedCache, TimeProvider.System);
        await using var limited = await RecoveryHarness.CreateAsync(limitedGate);
        FillIpWindow(limitedGate, IPhoneVerificationSendGate.StaffPasswordRecoveryScope);

        var result = await limited.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
        Assert.Equal(
            TwilioVerifyPhoneVerificationClient.RateLimitedMessage,
            status.Value?.GetType().GetProperty("error")?.GetValue(status.Value));
        Assert.False(limited.Queue.TryRead(out _));
        Assert.Empty(limited.Auth.FindCalls);
        Assert.Empty(limited.Auth.RecoveryCalls);
    }

    [Fact]
    public async Task Ip_window_uses_the_forwarded_for_first_hop()
    {
        var gate = new PhoneVerificationSendGate(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext.Request.Headers["X-Forwarded-For"] = "203.0.113.5, 10.0.0.1";
        await using var harness = await RecoveryHarness.CreateAsync(gate, accessor);
        FillIpWindow(gate, IPhoneVerificationSendGate.StaffPasswordRecoveryScope, "203.0.113.5");

        var result = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
        Assert.False(harness.Queue.TryRead(out _));
        Assert.Empty(harness.Auth.RecoveryCalls);
    }

    [Fact]
    public async Task Saturated_ip_returns_429_for_unknown_and_inactive_email()
    {
        var gate = new PhoneVerificationSendGate(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        await using var harness = await RecoveryHarness.CreateAsync(gate);
        FillIpWindow(gate, IPhoneVerificationSendGate.StaffPasswordRecoveryScope);

        var unknown = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "missing@club.test" },
            "club-a",
            CancellationToken.None);
        await harness.DeactivateAnaAAsync();
        var inactive = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);

        AssertRateLimited(unknown);
        AssertRateLimited(inactive);
        Assert.Equal(ErrorOf(unknown), ErrorOf(inactive));
        Assert.False(harness.Queue.TryRead(out _));
        Assert.Empty(harness.Auth.FindCalls);
        Assert.Empty(harness.Auth.RecoveryCalls);
    }

    [Fact]
    public async Task Missing_or_invalid_subdomain_does_not_spend_the_ip_window()
    {
        var gate = new PhoneVerificationSendGate(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        await using var harness = await RecoveryHarness.CreateAsync(gate);
        await harness.DeactivateClubBAsync();

        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts + 1; i++)
        {
            var noSubdomain = await harness.Controller.ForgotPassword(
                new ForgotPasswordRequestDto { Email = "ana@club.test" },
                null,
                CancellationToken.None);
            var invalidSubdomain = await harness.Controller.ForgotPassword(
                new ForgotPasswordRequestDto { Email = "ana@club.test" },
                "missing-club",
                CancellationToken.None);
            var inactiveSubdomain = await harness.Controller.ForgotPassword(
                new ForgotPasswordRequestDto { Email = "ana@club.test" },
                "club-b",
                CancellationToken.None);
            Assert.Equal(GenericMessage, MessageOf(noSubdomain));
            Assert.Equal(GenericMessage, MessageOf(invalidSubdomain));
            Assert.Equal(GenericMessage, MessageOf(inactiveSubdomain));
        }

        var allowed = await harness.Controller.ForgotPassword(
            new ForgotPasswordRequestDto { Email = "ana@club.test" },
            "club-a",
            CancellationToken.None);

        Assert.Equal(GenericMessage, MessageOf(allowed));
        Assert.Contains("Olá Ana A,", ReadSingle(harness.Queue).Body, StringComparison.Ordinal);
        Assert.Equal(["ana@club.test"], harness.Auth.FindCalls);
        Assert.Equal(["ana@club.test"], harness.Auth.RecoveryCalls);
    }

    [Fact]
    public async Task Eleven_fast_requests_match_for_active_inactive_and_unknown_email()
    {
        var active = await StaffBurstAsync("ana@club.test", deactivateMember: false);
        var inactive = await StaffBurstAsync("ana@club.test", deactivateMember: true);
        var unknown = await StaffBurstAsync("missing@club.test", deactivateMember: false);

        Assert.Equal(active.Statuses, inactive.Statuses);
        Assert.Equal(active.Statuses, unknown.Statuses);
        Assert.Equal(PhoneVerificationSendGate.IpMaxAttempts + 1, active.Statuses.Count);
        Assert.All(
            active.Statuses.Take(PhoneVerificationSendGate.IpMaxAttempts),
            status => Assert.Equal(StatusCodes.Status200OK, status));
        Assert.Equal(StatusCodes.Status429TooManyRequests, active.Statuses[^1]);
        Assert.Equal(1, active.EmailsQueued);
        Assert.Equal(0, inactive.EmailsQueued);
        Assert.Equal(0, unknown.EmailsQueued);
    }

    private static async Task<StaffBurst> StaffBurstAsync(string email, bool deactivateMember)
    {
        var gate = new PhoneVerificationSendGate(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System);
        await using var harness = await RecoveryHarness.CreateAsync(gate);
        if (deactivateMember)
        {
            await harness.DeactivateAnaAAsync();
        }

        var statuses = new List<int>(PhoneVerificationSendGate.IpMaxAttempts + 1);
        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts + 1; i++)
        {
            var result = await harness.Controller.ForgotPassword(
                new ForgotPasswordRequestDto { Email = email },
                "club-a",
                CancellationToken.None);
            statuses.Add(StatusCodeOf(result));
        }

        var queued = 0;
        while (harness.Queue.TryRead(out _))
        {
            queued++;
        }

        return new StaffBurst(statuses, queued);
    }

    private static int StatusCodeOf(ActionResult<ForgotPasswordResponseDto> result) =>
        result.Result switch
        {
            OkObjectResult ok => ok.StatusCode ?? StatusCodes.Status200OK,
            ObjectResult obj => obj.StatusCode ?? StatusCodes.Status500InternalServerError,
            _ => throw new InvalidOperationException(
                $"Unexpected forgot-password result {result.Result?.GetType().Name}."),
        };

    private sealed record StaffBurst(IReadOnlyList<int> Statuses, int EmailsQueued);

    private static void FillIpWindow(PhoneVerificationSendGate gate, string scope, string? clientIp = null)
    {
        var tenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            var decision = gate.Decide(tenantId, $"prefill{i}@club.test", clientIp, scope);
            Assert.Equal(PhoneVerificationSendDecision.Send, decision);
        }
    }

    private static PasswordRecoveryService CreateService(
        AppDbContext db,
        ITenantProvider tenantProvider,
        RecordingAuthAdmin auth,
        NotificationQueue queue,
        IPhoneVerificationSendGate? sendGate = null,
        IHttpContextAccessor? httpContextAccessor = null) =>
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
            sendGate ?? new AllowAllPhoneVerificationSendGate(),
            NullLogger<PasswordRecoveryService>.Instance,
            httpContextAccessor);

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

    private static void AssertRateLimited(ActionResult<ForgotPasswordResponseDto> result)
    {
        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
        Assert.Equal(TwilioVerifyPhoneVerificationClient.RateLimitedMessage, ErrorOf(result));
    }

    private static string? ErrorOf(ActionResult<ForgotPasswordResponseDto> result)
    {
        var status = Assert.IsType<ObjectResult>(result.Result);
        return status.Value?.GetType().GetProperty("error")?.GetValue(status.Value) as string;
    }

    private sealed class RecoveryHarness : IAsyncDisposable
    {
        private readonly AppDbContext _db;
        private readonly Tenant _tenantB;
        private readonly User _anaA;

        private RecoveryHarness(
            AppDbContext db,
            Tenant tenantB,
            User anaA,
            PasswordRecoveryController controller,
            NotificationQueue queue,
            RecordingAuthAdmin auth)
        {
            _db = db;
            _tenantB = tenantB;
            _anaA = anaA;
            Controller = controller;
            Queue = queue;
            Auth = auth;
        }

        public async Task DeactivateClubBAsync()
        {
            _tenantB.Deactivate();
            await _db.SaveChangesAsync();
        }

        public async Task DeactivateAnaAAsync()
        {
            _anaA.Deactivate();
            await _db.SaveChangesAsync();
        }

        public ValueTask DisposeAsync() => _db.DisposeAsync();

        public PasswordRecoveryController Controller { get; }

        public NotificationQueue Queue { get; }

        public RecordingAuthAdmin Auth { get; }

        public static async Task<RecoveryHarness> CreateAsync(
            IPhoneVerificationSendGate? sendGate = null,
            IHttpContextAccessor? httpContextAccessor = null)
        {
            var tenantA = new Tenant("Club A", "66666666000191", subdomain: "club-a");
            var tenantB = new Tenant("Club B", "11222333000181", subdomain: "club-b");
            var anaA = new User(tenantA.Id, "auth-ana", "Ana A", "ana@club.test");
            var ambient = new AmbientTenantContext();
            var tenantProvider = new HttpContextTenantProvider(
                new HttpContextAccessor(),
                ambient,
                new FakePlatformAdminChecker(),
                new PlatformAdminTenantSession());
            var db = InMemoryAppDb.Create(tenantProvider);
            db.Tenants.AddRange(tenantA, tenantB);
            db.Users.Add(anaA);
            db.Users.Add(new User(tenantA.Id, "auth-only-a", "Only A", "only-a@club.test"));
            db.Users.Add(new User(tenantB.Id, "auth-ana", "Ana B", "ana@club.test"));
            await db.SaveChangesAsync();

            var auth = new RecordingAuthAdmin { AuthUserId = "auth-ana" };
            var queue = new NotificationQueue();
            var service = CreateService(db, tenantProvider, auth, queue, sendGate, httpContextAccessor);
            var binder = new PublicTenantBinder(db, ambient);
            var controller = new PasswordRecoveryController(service, binder);
            return new RecoveryHarness(db, tenantB, anaA, controller, queue, auth);
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

        public Task<Guid?> GetUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);

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
