using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
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
