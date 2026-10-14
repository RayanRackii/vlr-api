using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Modules.Customers.Controllers;
using Platform.Api.Modules.Customers.Services;
using Platform.Api.Modules.Users.Controllers;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Request-level follow-up for #149. The access token always carries a stale
/// tenant claim. The effective tenant comes only from live Auth metadata plus
/// an active membership.
/// </summary>
public sealed class PlatformAdminTenantResolutionEdgeTests
{
    private const string AdminEmail = "super@rolvix.test";
    private const string AuthId = "auth-super-1";

    [Fact]
    public async Task Auth_read_timeout_falls_back_to_platform_mode_and_ignores_stale_jwt()
    {
        await AssertAuthReadFailureStaysInPlatformModeAsync(
            new SupabaseAuthAdminException(
                "Timed out reading Supabase user app_metadata.",
                504));
    }

    [Fact]
    public async Task Auth_read_failure_falls_back_to_platform_mode_and_ignores_stale_jwt()
    {
        await AssertAuthReadFailureStaysInPlatformModeAsync(
            new InvalidOperationException("auth-admin-read-failed"));
    }

    [Fact]
    public async Task Inactive_membership_does_not_open_that_tenant()
    {
        var seed = await SeedAsync(activeMembership: false);
        var auth = new ScriptedAuthAdmin();
        auth.SetTenant(AuthId, seed.TenantA);
        using var host = await StartHostAsync(seed.DatabaseName, auth);

        await AssertPlatformModeAsync(host, seed.TenantB);
        Assert.True(auth.ReadCalls > 0);

        await using var stored = InMemoryAppDb.Create(new FakeTenantProvider(), seed.DatabaseName);
        var names = await stored.Customers.IgnoreQueryFilters().Select(customer => customer.Name).ToListAsync();
        Assert.Contains("Ana Portal", names);
        Assert.Contains("Bruno Other", names);
    }

    [Fact]
    public async Task Deleted_tenant_after_enter_has_no_context_and_no_cross_tenant_data()
    {
        var seed = await SeedAsync(activeMembership: true);
        var auth = new ScriptedAuthAdmin();
        auth.SetTenant(AuthId, seed.TenantA);
        using var host = await StartHostAsync(seed.DatabaseName, auth);

        var inside = await GetAsync(host, "/api/users/me", seed.TenantB);
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        using (var insideDoc = JsonDocument.Parse(await inside.Content.ReadAsStringAsync()))
        {
            Assert.Equal(seed.TenantA, insideDoc.RootElement.GetProperty("tenantId").GetGuid());
            Assert.Equal("ADMIN", insideDoc.RootElement.GetProperty("role").GetString());
        }

        var customers = await GetAsync(host, "/api/customers", seed.TenantB);
        Assert.Equal(HttpStatusCode.OK, customers.StatusCode);
        var customerBody = await customers.Content.ReadAsStringAsync();
        Assert.Contains("Ana Portal", customerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Bruno Other", customerBody, StringComparison.Ordinal);

        await DeleteTenantGraphAsync(seed.DatabaseName, seed.TenantA);
        await AssertOtherTenantStillStoredAsync(seed);

        await AssertPlatformModeAsync(host, seed.TenantB);
        Assert.True(auth.ReadCalls > 0);
    }

    [Fact]
    public async Task Active_membership_left_after_tenant_delete_stays_in_platform_mode()
    {
        var seed = await SeedAsync(activeMembership: true);
        var auth = new ScriptedAuthAdmin();
        auth.SetTenant(AuthId, seed.TenantA);
        using var host = await StartHostAsync(seed.DatabaseName, auth);

        await DeleteTenantOnlyAsync(seed.DatabaseName, seed.TenantA);
        await AssertOrphanMembershipRemainsAsync(seed);

        await AssertPlatformModeAsync(host, seed.TenantB);
        Assert.True(auth.ReadCalls > 0);
        Assert.Equal(AuthId, Assert.Single(auth.ReadIds.Distinct()));

        await AssertOrphanMembershipRemainsAsync(seed);
    }

    [Fact]
    public void Orphan_membership_lookup_selects_only_when_the_tenant_row_exists()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new AppDbContext(options, new FakeTenantProvider());
        var tenantId = Guid.NewGuid();
        var sql = db.Users
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(user => user.TenantId == tenantId
                && user.SupabaseAuthId == AuthId
                && user.IsActive
                && db.Tenants.Any(tenant => tenant.Id == tenantId))
            .ToQueryString();

        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(statements);
        Assert.Contains("core.users", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("core.tenants", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXISTS", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is_active", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("supabase_auth_id", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Malformed_sub_does_not_leak_or_select_a_tenant()
    {
        const string malformed = "not-a-guid";
        var seed = await SeedAsync(activeMembership: true);
        var auth = new ScriptedAuthAdmin
        {
            ReadFailure = new SupabaseAuthAdminException(
                "Timed out reading Supabase user app_metadata.",
                504),
        };
        auth.SetTenant(AuthId, seed.TenantA);
        auth.SetTenant(malformed, seed.TenantA);
        using var host = await StartHostAsync(seed.DatabaseName, auth);

        var me = await GetAsync(host, "/api/users/me", seed.TenantB, sub: malformed);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meBody = await me.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Timed out reading", meBody, StringComparison.Ordinal);
        Assert.DoesNotContain(malformed, meBody, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", meBody, StringComparison.Ordinal);
        using (var meDoc = JsonDocument.Parse(meBody))
        {
            Assert.Equal(JsonValueKind.Null, meDoc.RootElement.GetProperty("tenantId").ValueKind);
            Assert.Equal("SUPER_ADMIN", meDoc.RootElement.GetProperty("role").GetString());
        }

        var customers = await GetAsync(host, "/api/customers", seed.TenantB, sub: malformed);
        Assert.Equal(HttpStatusCode.Unauthorized, customers.StatusCode);
        var customerBody = await customers.Content.ReadAsStringAsync();
        Assert.Contains("Tenant context is required.", customerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana Portal", customerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Bruno Other", customerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Timed out reading", customerBody, StringComparison.Ordinal);
        Assert.Equal(malformed, Assert.Single(auth.ReadIds.Distinct()));
    }

    [Fact]
    public async Task Non_guid_sub_with_live_metadata_does_not_select_another_membership()
    {
        const string malformed = "not-a-guid";
        var seed = await SeedAsync(activeMembership: true);
        var auth = new ScriptedAuthAdmin();
        auth.SetTenant(AuthId, seed.TenantA);
        auth.SetTenant(malformed, seed.TenantA);
        using var host = await StartHostAsync(seed.DatabaseName, auth);

        await AssertPlatformModeAsync(host, seed.TenantB, sub: malformed);
        Assert.Contains(malformed, auth.ReadIds);
        Assert.DoesNotContain(AuthId, auth.ReadIds);
    }

    [Fact]
    public async Task Empty_sub_stays_in_platform_mode_without_calling_auth()
    {
        var seed = await SeedAsync(activeMembership: true);
        var auth = new ScriptedAuthAdmin();
        auth.SetTenant(AuthId, seed.TenantA);
        auth.SetTenant(string.Empty, seed.TenantA);
        using var host = await StartHostAsync(seed.DatabaseName, auth);

        await AssertPlatformModeAsync(host, seed.TenantB, omitSub: true);
        Assert.Equal(0, auth.ReadCalls);
    }

    private static async Task AssertAuthReadFailureStaysInPlatformModeAsync(Exception failure)
    {
        var seed = await SeedAsync(activeMembership: true);
        var auth = new ScriptedAuthAdmin { ReadFailure = failure };
        auth.SetTenant(AuthId, seed.TenantA);
        using var host = await StartHostAsync(seed.DatabaseName, auth);

        await AssertPlatformModeAsync(host, seed.TenantB);
        Assert.True(auth.ReadCalls > 0);
        Assert.Equal(AuthId, Assert.Single(auth.ReadIds.Distinct()));
    }

    private static async Task AssertPlatformModeAsync(
        IHost host,
        Guid staleJwtTenantId,
        string? sub = null,
        bool omitSub = false)
    {
        var me = await GetAsync(host, "/api/users/me", staleJwtTenantId, sub, omitSub);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meBody = await me.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Ana Portal", meBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Bruno Other", meBody, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", meBody, StringComparison.Ordinal);
        using (var meDoc = JsonDocument.Parse(meBody))
        {
            Assert.Equal(JsonValueKind.Null, meDoc.RootElement.GetProperty("tenantId").ValueKind);
            Assert.Equal("SUPER_ADMIN", meDoc.RootElement.GetProperty("role").GetString());
        }

        var customers = await GetAsync(host, "/api/customers", staleJwtTenantId, sub, omitSub);
        Assert.Equal(HttpStatusCode.Unauthorized, customers.StatusCode);
        var customerBody = await customers.Content.ReadAsStringAsync();
        Assert.Contains("Tenant context is required.", customerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Ana Portal", customerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Bruno Other", customerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", customerBody, StringComparison.Ordinal);
    }

    private static async Task AssertOtherTenantStillStoredAsync(Seed seed)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), seed.DatabaseName);
        var names = await db.Customers
            .IgnoreQueryFilters()
            .Select(customer => customer.Name)
            .ToListAsync();
        Assert.Contains("Bruno Other", names);
        Assert.DoesNotContain("Ana Portal", names);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        IHost host,
        string path,
        Guid staleTenantId,
        string? sub = null,
        bool omitSub = false)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(StaleTokenHandler.UserHeader, AdminEmail);
        client.DefaultRequestHeaders.Add(StaleTokenHandler.TenantHeader, staleTenantId.ToString());
        if (omitSub)
        {
            client.DefaultRequestHeaders.Add(StaleTokenHandler.SubHeader, StaleTokenHandler.OmitSub);
        }
        else if (sub is not null)
        {
            client.DefaultRequestHeaders.Add(StaleTokenHandler.SubHeader, sub);
        }

        return await client.GetAsync(path);
    }

    private static async Task<Seed> SeedAsync(bool activeMembership)
    {
        var databaseName = $"tenant-resolution-{Guid.NewGuid():N}";
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        var tenantA = new Tenant("Club A", "11111111000191", subdomain: "cluba");
        var tenantB = new Tenant("Club B", "22222222000191", subdomain: "clubb");
        db.Tenants.AddRange(tenantA, tenantB);
        var membership = new User(tenantA.Id, AuthId, "Super Admin", AdminEmail);
        if (!activeMembership)
        {
            membership.Deactivate();
        }

        db.Users.Add(membership);
        db.Customers.AddRange(
            new Customer
            {
                TenantId = tenantA.Id,
                Name = "Ana Portal",
                Email = "ana.portal@club.test",
            },
            new Customer
            {
                TenantId = tenantB.Id,
                Name = "Bruno Other",
                Email = "bruno.other@club.test",
            });
        await db.SaveChangesAsync();
        return new Seed(databaseName, tenantA.Id, tenantB.Id);
    }

    private static async Task DeleteTenantOnlyAsync(string databaseName, Guid tenantId)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        var tenant = await db.Tenants.SingleAsync(item => item.Id == tenantId);
        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync();
    }

    private static async Task AssertOrphanMembershipRemainsAsync(Seed seed)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), seed.DatabaseName);
        var membership = await db.Users
            .IgnoreQueryFilters()
            .SingleAsync(user => user.SupabaseAuthId == AuthId);
        Assert.Equal(seed.TenantA, membership.TenantId);
        Assert.True(membership.IsActive);
        Assert.False(await db.Tenants.AnyAsync(tenant => tenant.Id == seed.TenantA));
    }

    private static async Task DeleteTenantGraphAsync(string databaseName, Guid tenantId)
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider(), databaseName);
        var users = await db.Users
            .IgnoreQueryFilters()
            .Where(user => user.TenantId == tenantId)
            .ToListAsync();
        var customers = await db.Customers
            .IgnoreQueryFilters()
            .Where(customer => customer.TenantId == tenantId)
            .ToListAsync();
        db.Users.RemoveRange(users);
        db.Customers.RemoveRange(customers);
        var tenant = await db.Tenants.SingleAsync(item => item.Id == tenantId);
        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync();
    }

    private static async Task<IHost> StartHostAsync(string databaseName, ScriptedAuthAdmin auth)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddSingleton<IHostEnvironment, FakeHostEnvironment>();
                    services.AddSingleton<ISupabaseAuthAdminClient>(auth);
                    services.Configure<PlatformAdminOptions>(options => options.Emails = [AdminEmail]);
                    services.AddSingleton<IPlatformAdminChecker, PlatformAdminChecker>();
                    services.AddSingleton<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
                    services.AddSingleton<NotificationQueue>();
                    services.AddSingleton<ITrialGuard, FakeTrialGuard>();
                    services.AddSingleton<ITenantAccessBootstrapper, FakeTenantAccessBootstrapper>();
                    services.AddSingleton<ITenantUserAdminService, FakeTenantUserAdminService>();
                    services.AddScoped<AmbientTenantContext>();
                    services.AddScoped<PlatformAdminTenantSession>();
                    services.AddScoped<ITenantProvider, HttpContextTenantProvider>();
                    services.AddScoped<AppDbContext>(sp =>
                        InMemoryAppDb.Create(sp.GetRequiredService<ITenantProvider>(), databaseName));
                    services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
                    services.AddScoped<IPermissionResolver, PermissionResolver>();
                    services.AddScoped<IRbacGrantGuard, RbacGrantGuard>();
                    services.AddScoped<IRbacActorAccessor, RbacActorAccessor>();
                    services.AddScoped<IPlatformAdminMembershipService, PlatformAdminMembershipService>();
                    services.AddScoped<IAdminTenantService, AdminTenantService>();
                    services.AddScoped<IUserDirectoryService, UserDirectoryService>();
                    services.AddScoped<ITenantCustomerDirectory, TenantCustomerDirectory>();
                    services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
                    services.AddAuthorization(options => options.AddRolvixPolicies());
                    services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
                    services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
                    services
                        .AddAuthentication(SupabaseJwtBearerDefaults.AuthenticationScheme)
                        .AddScheme<AuthenticationSchemeOptions, StaleTokenHandler>(
                            SupabaseJwtBearerDefaults.AuthenticationScheme,
                            _ => { });
                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(UsersController).Assembly));
                            manager.FeatureProviders.Add(new SelectedControllers(
                                typeof(UsersController),
                                typeof(TenantCustomersController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseMiddleware<PlatformAdminTenantSessionMiddleware>();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });
        return await host.StartAsync();
    }

    private sealed record Seed(string DatabaseName, Guid TenantA, Guid TenantB);

    private sealed class SelectedControllers(params Type[] controllerTypes) : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) =>
            controllerTypes.Contains(typeInfo.AsType());
    }

    private sealed class StaleTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string UserHeader = "X-Test-User";
        public const string TenantHeader = "X-Test-Tenant";
        public const string SubHeader = "X-Test-Sub";
        public const string OmitSub = "omit";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var userValues)
                || string.IsNullOrWhiteSpace(userValues.ToString()))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var email = userValues.ToString();
            var claims = new List<Claim>
            {
                new("email", email),
                new(ClaimTypes.Email, email),
                new(ClaimTypes.Name, email),
            };

            var includeSub = true;
            var sub = AuthId;
            if (Request.Headers.TryGetValue(SubHeader, out var subValues))
            {
                var raw = subValues.ToString();
                if (raw == OmitSub)
                {
                    includeSub = false;
                }
                else
                {
                    sub = raw;
                }
            }

            if (includeSub)
            {
                claims.Add(new Claim("sub", sub));
            }

            if (Request.Headers.TryGetValue(TenantHeader, out var tenantValues)
                && Guid.TryParse(tenantValues.ToString(), out var tenantId))
            {
                claims.Add(new Claim(
                    TenantClaimTypes.AppMetadata,
                    $"{{\"tenant_id\":\"{tenantId}\"}}"));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class ScriptedAuthAdmin : ISupabaseAuthAdminClient
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
            Task.FromResult<string?>(AuthId);

        public Task<bool> UserExistsAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task UpdateUserAppMetadataAsync(
            string supabaseUserId,
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            _tenants[supabaseUserId] = tenantId;
            return Task.CompletedTask;
        }

        public Task ClearUserTenantAppMetadataAsync(
            string supabaseUserId,
            CancellationToken cancellationToken = default)
        {
            _tenants[supabaseUserId] = null;
            return Task.CompletedTask;
        }

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
}
