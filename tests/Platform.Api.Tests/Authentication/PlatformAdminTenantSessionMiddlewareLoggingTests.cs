using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Authentication;

public sealed class PlatformAdminTenantSessionMiddlewareLoggingTests
{
    private const string AdminEmail = "super@rolvix.test";
    private const string AuthId = "auth-super-1";

    [Fact]
    public async Task Catch_logs_the_exception_object_and_withholds_tenant()
    {
        var failure = new SupabaseAuthAdminException(
            "Timed out reading Supabase user app_metadata.",
            504);
        var logger = new CaptureLogger<PlatformAdminTenantSessionMiddleware>();
        var session = new PlatformAdminTenantSession();
        var continued = false;

        await InvokeAsync(
            new ThrowingAuthAdminClient(failure),
            session,
            logger,
            httpContext => Task.CompletedTask,
            onNext: () => continued = true);

        Assert.True(continued);
        Assert.True(session.IsResolved);
        Assert.Null(session.TenantId);
        var entry = Assert.Single(logger.Entries);
        Assert.Same(failure, entry.Exception);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Tenant context withheld", entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SupabaseAuthAdminException), entry.Message, StringComparison.Ordinal);
        Assert.Contains("504", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthId, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminEmail, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cache_hit_still_runs_the_live_membership_query()
    {
        var authId = Guid.NewGuid().ToString("D");
        var databaseName = $"membership-cache-{Guid.NewGuid():N}";
        var tenant = new Tenant("Club A", "11111111000191", subdomain: "cluba");
        var handler = new CountingTenantHandler { TenantId = tenant.Id };
        var cache = new SupabaseUserTenantMetadataCache();
        var client = CreateAuthClient(handler, cache);

        await using (var seed = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            seed.Tenants.Add(tenant);
            seed.Users.Add(new User(tenant.Id, authId, "Super Admin", AdminEmail));
            await seed.SaveChangesAsync();
        }

        var active = await InvokeResolvedAsync(client, authId, databaseName);
        Assert.Equal(tenant.Id, active.TenantId);
        Assert.Equal(1, handler.Gets);

        await using (var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            var member = await db.Users.IgnoreQueryFilters().SingleAsync(user => user.SupabaseAuthId == authId);
            member.Deactivate();
            await db.SaveChangesAsync();
        }

        var inactive = await InvokeResolvedAsync(client, authId, databaseName);
        Assert.Null(inactive.TenantId);
        Assert.Equal(1, handler.Gets);

        await using (var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            var member = await db.Users.IgnoreQueryFilters().SingleAsync(user => user.SupabaseAuthId == authId);
            member.Activate();
            await db.SaveChangesAsync();
        }

        var restored = await InvokeResolvedAsync(client, authId, databaseName);
        Assert.Equal(tenant.Id, restored.TenantId);
        Assert.Equal(1, handler.Gets);
    }

    [Fact]
    public async Task Active_membership_whose_tenant_row_is_gone_does_not_resolve()
    {
        var authId = Guid.NewGuid().ToString("D");
        var databaseName = $"orphan-membership-{Guid.NewGuid():N}";
        var handler = new CountingTenantHandler();
        var client = CreateAuthClient(handler, new SupabaseUserTenantMetadataCache());

        await using (var seed = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            var tenant = new Tenant("Club A", "11111111000191", subdomain: "cluba");
            seed.Tenants.Add(tenant);
            seed.Users.Add(new User(tenant.Id, authId, "Super Admin", AdminEmail));
            await seed.SaveChangesAsync();
            handler.TenantId = tenant.Id;
        }

        await using (var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            var tenant = await db.Tenants.SingleAsync();
            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync();
        }

        var session = await InvokeResolvedAsync(client, authId, databaseName);

        Assert.Null(session.TenantId);
        Assert.Equal(1, handler.Gets);
    }

    [Fact]
    public async Task Request_abort_is_not_logged_as_a_tenant_failure()
    {
        var logger = new CaptureLogger<PlatformAdminTenantSessionMiddleware>();
        var session = new PlatformAdminTenantSession();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeAsync(
                new ThrowingAuthAdminClient(new OperationCanceledException(abort.Token)),
                session,
                logger,
                context =>
                {
                    context.RequestAborted = abort.Token;
                    return Task.CompletedTask;
                },
                onNext: () => { }));

        Assert.False(session.IsResolved);
        Assert.Empty(logger.Entries);
        Assert.IsType<OperationCanceledException>(exception);
    }

    private static async Task InvokeAsync(
        ISupabaseAuthAdminClient auth,
        PlatformAdminTenantSession session,
        CaptureLogger<PlatformAdminTenantSessionMiddleware> logger,
        Func<HttpContext, Task> configure,
        Action onNext)
    {
        var middleware = new PlatformAdminTenantSessionMiddleware(_ =>
        {
            onNext();
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new Claim("sub", AuthId),
                        new Claim("email", AdminEmail),
                    ],
                    authenticationType: "Test")),
        };
        await configure(context);

        var checker = new PlatformAdminChecker(
            Options.Create(new PlatformAdminOptions { Emails = [AdminEmail] }));
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());

        await middleware.InvokeAsync(context, session, checker, auth, db, logger);
    }

    private static async Task<PlatformAdminTenantSession> InvokeResolvedAsync(
        ISupabaseAuthAdminClient auth,
        string authId,
        string databaseName)
    {
        var middleware = new PlatformAdminTenantSessionMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [
                        new Claim("sub", authId),
                        new Claim("email", AdminEmail),
                    ],
                    authenticationType: "Test")),
        };
        var session = new PlatformAdminTenantSession();
        var checker = new PlatformAdminChecker(
            Options.Create(new PlatformAdminOptions { Emails = [AdminEmail] }));
        var logger = new CaptureLogger<PlatformAdminTenantSessionMiddleware>();
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);

        await middleware.InvokeAsync(context, session, checker, auth, db, logger);

        Assert.True(session.IsResolved);
        Assert.Empty(logger.Entries);
        return session;
    }

    private static SupabaseAuthAdminClient CreateAuthClient(
        HttpMessageHandler handler,
        SupabaseUserTenantMetadataCache cache)
    {
        var http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://auth.example.test/auth/v1/"),
        };
        return new SupabaseAuthAdminClient(
            http,
            Options.Create(new SupabaseOptions
            {
                Url = "https://auth.example.test",
                JwtSecret = "test-jwt-secret",
                ServiceRoleKey = "eyJ-test-service-role-key",
            }),
            cache);
    }

    private sealed class CountingTenantHandler : HttpMessageHandler
    {
        public int Gets { get; private set; }

        public Guid TenantId { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Gets++;
            }

            var body = "{\"app_metadata\":{\"tenant_id\":\"" + TenantId.ToString("D") + "\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingAuthAdminClient(Exception failure) : ISupabaseAuthAdminClient
    {
        public Task<string> CreateUserAsync(
            string email,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(failure);

        public Task<string?> FindUserIdByEmailAsync(
            string email,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string?>(failure);

        public Task<bool> UserExistsAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(failure);

        public Task UpdateUserAppMetadataAsync(
            string supabaseUserId,
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromException(failure);

        public Task ClearUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromException(failure);

        public Task<Guid?> GetUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Guid?>(failure);

        public Task SetUserPasswordAsync(
            string supabaseUserId,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromException(failure);

        public Task DeleteUserAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromException(failure);

        public Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
            string email,
            string redirectTo,
            CancellationToken cancellationToken = default) =>
            Task.FromException<SupabaseRecoveryLink>(failure);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

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
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
