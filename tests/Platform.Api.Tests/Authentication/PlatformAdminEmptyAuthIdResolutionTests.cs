using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Authentication;

public sealed class PlatformAdminEmptyAuthIdResolutionTests
{
    private const string AdminEmail = "super@rolvix.test";
    private const string RealAuthId = "auth-super-1";

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task Blank_sub_resolves_session_without_tenant_or_auth_call(string sub)
    {
        var result = await ResolveAsync(sub, includeSub: true, nameIdentifier: null);

        AssertWithheldWithoutAuthCall(result);
    }

    [Fact]
    public async Task Missing_sub_and_name_identifier_resolves_session_without_tenant_or_auth_call()
    {
        var result = await ResolveAsync(sub: null, includeSub: false, nameIdentifier: null);

        AssertWithheldWithoutAuthCall(result);
    }

    [Fact]
    public async Task Blank_sub_does_not_adopt_name_identifier_or_stale_tenant()
    {
        var result = await ResolveAsync(sub: "", includeSub: true, nameIdentifier: RealAuthId);

        AssertWithheldWithoutAuthCall(result);
        Assert.Equal(0, result.Auth.ReadCalls);
    }

    [Theory]
    [InlineData("auth/evil")]
    [InlineData("auth?tenant=1")]
    public async Task Path_shaped_subject_is_withheld_like_an_empty_auth_id(string sub)
    {
        var result = await ResolveAsync(sub, includeSub: true, nameIdentifier: RealAuthId);

        AssertWithheldWithoutAuthCall(result);
        Assert.DoesNotContain(sub, result.Logger.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public async Task Malformed_sub_auth_failure_does_not_leak_or_select_stale_tenant()
    {
        const string malformed = "not-a-guid";
        var failure = new SupabaseAuthAdminException(
            "Timed out reading Supabase user app_metadata.",
            504);
        var result = await ResolveAsync(
            malformed,
            includeSub: true,
            nameIdentifier: null,
            readFailure: failure);

        Assert.True(result.Continued);
        Assert.True(result.Session.IsResolved);
        Assert.Null(result.Session.TenantId);
        Assert.Null(result.Provider.TenantId);
        Assert.Equal(malformed, Assert.Single(result.Auth.ReadIds));
        var entry = Assert.Single(result.Logger.Entries);
        Assert.Same(failure, entry.Exception);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(malformed, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.StaleTenantId.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminEmail, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", entry.Message, StringComparison.Ordinal);
    }

    private static void AssertWithheldWithoutAuthCall(Resolved result)
    {
        Assert.True(result.Continued);
        Assert.True(result.Session.IsResolved);
        Assert.Null(result.Session.TenantId);
        Assert.Null(result.Provider.TenantId);
        Assert.Equal(0, result.Auth.ReadCalls);
        Assert.Empty(result.Logger.Entries);
    }

    private static async Task<Resolved> ResolveAsync(
        string? sub,
        bool includeSub,
        string? nameIdentifier,
        Exception? readFailure = null)
    {
        var staleTenantId = Guid.NewGuid();
        var databaseName = $"empty-auth-{Guid.NewGuid():N}";
        Guid liveTenantId;
        await using (var seed = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
        {
            var tenant = new Tenant("Club A", "11111111000191", subdomain: "cluba");
            seed.Tenants.Add(tenant);
            seed.Users.Add(new User(tenant.Id, RealAuthId, "Super Admin", AdminEmail));
            await seed.SaveChangesAsync();
            liveTenantId = tenant.Id;
        }

        var auth = new RecordingAuthAdmin();
        auth.SetTenant(RealAuthId, liveTenantId);
        auth.ReadFailure = readFailure;
        var session = new PlatformAdminTenantSession();
        var logger = new CaptureLogger<PlatformAdminTenantSessionMiddleware>();
        var continued = false;
        var claims = new List<Claim>
        {
            new("email", AdminEmail),
            new(
                TenantClaimTypes.AppMetadata,
                $"{{\"tenant_id\":\"{staleTenantId:D}\"}}"),
        };
        if (includeSub)
        {
            claims.Add(new Claim("sub", sub ?? string.Empty));
        }

        if (nameIdentifier is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
        };
        var checker = new PlatformAdminChecker(
            Options.Create(new PlatformAdminOptions { Emails = [AdminEmail] }));
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        var middleware = new PlatformAdminTenantSessionMiddleware(_ =>
        {
            continued = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, session, checker, auth, db, logger);

        var provider = new HttpContextTenantProvider(
            new FakeHttpContextAccessor { HttpContext = context },
            new AmbientTenantContext(),
            checker,
            session);

        return new Resolved(continued, session, provider, auth, logger, staleTenantId);
    }

    private sealed record Resolved(
        bool Continued,
        PlatformAdminTenantSession Session,
        HttpContextTenantProvider Provider,
        RecordingAuthAdmin Auth,
        CaptureLogger<PlatformAdminTenantSessionMiddleware> Logger,
        Guid StaleTenantId);

    private sealed class RecordingAuthAdmin : ISupabaseAuthAdminClient
    {
        private readonly Dictionary<string, Guid?> _tenants = new(StringComparer.Ordinal);

        public Exception? ReadFailure { get; set; }

        public int ReadCalls { get; private set; }

        public List<string> ReadIds { get; } = [];

        public void SetTenant(string authId, Guid? tenantId) => _tenants[authId] = tenantId;

        public Task<Guid?> GetUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            ReadIds.Add(supabaseUserId);
            if (ReadFailure is not null)
            {
                return Task.FromException<Guid?>(ReadFailure);
            }

            return Task.FromResult(
                _tenants.TryGetValue(supabaseUserId, out var tenantId) ? tenantId : null);
        }

        public Task<string> CreateUserAsync(
            string email,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("unused");

        public Task<string?> FindUserIdByEmailAsync(
            string email,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> UserExistsAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task UpdateUserAppMetadataAsync(
            string supabaseUserId,
            Guid tenantId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetUserPasswordAsync(
            string supabaseUserId,
            string password,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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
