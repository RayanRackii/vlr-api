using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Platform.Api.Authentication;
using Platform.Api.Tests.CustomerAuth;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Authentication;

public sealed class CustomerJwtSessionValidatorTests
{
    private const string JwtSecret = "customer-jwt-session-test-secret-32b!";
    private const string MemberEmail = "member@club.test";
    private const string MemberPhone = "+5511999990000";
    private const string Canary = "password=super-secret-store-canary";

    [Fact]
    public async Task Stamp_mismatch_fails_the_session_without_an_error_log()
    {
        var logger = new ListLogger();
        var changedAt = DateTimeOffset.Parse("2026-10-15T12:00:00.123+00:00");
        var validator = new CustomerJwtSessionValidator(
            (_, _, _) => Task.FromResult<CustomerPasswordStampRow?>(new CustomerPasswordStampRow(changedAt)),
            logger);
        var context = Context(Principal(Guid.NewGuid(), Guid.NewGuid()));

        await validator.ValidateAsync(context);

        Assert.Equal("Session expired.", context.Result?.Failure?.Message);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Missing_stamp_row_fails_the_session_without_an_error_log()
    {
        var logger = new ListLogger();
        var validator = new CustomerJwtSessionValidator(
            (_, _, _) => Task.FromResult<CustomerPasswordStampRow?>(null),
            logger);
        var context = Context(Principal(Guid.NewGuid(), Guid.NewGuid()));

        await validator.ValidateAsync(context);

        Assert.Equal("Session expired.", context.Result?.Failure?.Message);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Invalid_claims_fail_before_the_store_is_read()
    {
        var logger = new ListLogger();
        var called = false;
        var validator = new CustomerJwtSessionValidator(
            (_, _, _) =>
            {
                called = true;
                return Task.FromResult<CustomerPasswordStampRow?>(null);
            },
            logger);
        var context = Context(new ClaimsPrincipal(new ClaimsIdentity()));

        await validator.ValidateAsync(context);

        Assert.False(called);
        Assert.Equal("Session expired.", context.Result?.Failure?.Message);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Matching_stamp_does_not_fail_the_session()
    {
        var changedAt = DateTimeOffset.Parse("2026-10-15T12:00:00.123+00:00");
        var validator = new CustomerJwtSessionValidator(
            (_, _, _) => Task.FromResult<CustomerPasswordStampRow?>(new CustomerPasswordStampRow(changedAt)),
            new ListLogger());
        var context = Context(Principal(
            Guid.NewGuid(),
            Guid.NewGuid(),
            CustomerPasswordStamp.Format(changedAt)));

        await validator.ValidateAsync(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public async Task Request_abort_propagates_and_is_not_logged_as_a_store_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var logger = new ListLogger();
        var validator = new CustomerJwtSessionValidator(
            (_, _, token) => throw new OperationCanceledException(token),
            logger);
        var context = Context(Principal(Guid.NewGuid(), Guid.NewGuid()), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validator.ValidateAsync(context));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task Store_timeout_becomes_a_sanitized_500_exception()
    {
        var logger = new ListLogger();
        var validator = new CustomerJwtSessionValidator(
            (_, _, _) => throw new OperationCanceledException(Canary),
            logger);
        var context = Context(Principal(Guid.NewGuid(), Guid.NewGuid()));

        var exception = await Assert.ThrowsAsync<CustomerSessionStoreUnavailableException>(
            () => validator.ValidateAsync(context));

        AssertStoreFailureIsSafe(exception, logger);
    }

    [Fact]
    public async Task Postgres_failure_logs_sqlstate_and_not_the_exception_message()
    {
        var logger = new ListLogger();
        var postgres = new PostgresException(
            Canary,
            "ERROR",
            "ERROR",
            PostgresErrorCodes.ConnectionFailure);
        var validator = new CustomerJwtSessionValidator(
            (_, _, _) => throw new InvalidOperationException("wrapper " + Canary, postgres),
            logger);
        var context = Context(Principal(Guid.NewGuid(), Guid.NewGuid()));

        var exception = await Assert.ThrowsAsync<CustomerSessionStoreUnavailableException>(
            () => validator.ValidateAsync(context));

        AssertStoreFailureIsSafe(exception, logger);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("ExceptionType=System.InvalidOperationException", entry.Message, StringComparison.Ordinal);
        Assert.Contains("SqlState=08006", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsafe_sqlstate_is_not_logged()
    {
        var logger = new ListLogger();
        var postgres = new PostgresException(Canary, "ERROR", "ERROR", "secrt");
        var validator = new CustomerJwtSessionValidator(
            (_, _, _) => throw new InvalidOperationException(Canary, postgres),
            logger);

        await Assert.ThrowsAsync<CustomerSessionStoreUnavailableException>(
            () => validator.ValidateAsync(Context(Principal(Guid.NewGuid(), Guid.NewGuid()))));

        var entry = Assert.Single(logger.Entries);
        Assert.DoesNotContain("SqlState=", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secrt", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Disposed_store_becomes_a_sanitized_500_exception()
    {
        var (db, _, customer) = await SeedAsync();
        await db.DisposeAsync();
        var logger = new ListLogger();
        var validator = new CustomerJwtSessionValidator(db, logger);

        var exception = await Assert.ThrowsAsync<CustomerSessionStoreUnavailableException>(
            () => validator.ValidateAsync(Context(Principal(customer.Id, customer.TenantId))));

        AssertStoreFailureIsSafe(exception, logger);
        var entry = Assert.Single(logger.Entries);
        Assert.DoesNotContain("Cannot access a disposed context", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberEmail, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(customer.Id.ToString(), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_session_is_http_200()
    {
        var (db, _, customer) = await SeedAsync();
        await using var _ = db;
        var logs = new SecretLogCapture();
        using var host = await StartAsync(logs, db);
        var token = Issue(customer);

        var response = await SendAsync(host, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoSecrets(logs, await response.Content.ReadAsStringAsync(), token, MemberEmail, MemberPhone, Canary);
    }

    [Fact]
    public async Task Stamp_mismatch_is_http_401()
    {
        var (db, _, customer) = await SeedAsync();
        await using var _ = db;
        var token = Issue(customer);
        customer.PasswordChangedAt = DateTimeOffset.Parse("2026-10-15T15:00:00.000+00:00");
        await db.SaveChangesAsync();
        var logs = new SecretLogCapture();
        using var host = await StartAsync(logs, db);

        var response = await SendAsync(host, token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("An unexpected error occurred.", body, StringComparison.Ordinal);
        AssertNoSecrets(logs, body, token, MemberEmail, MemberPhone, Canary);
        Assert.DoesNotContain(
            logs.Entries,
            entry => entry.Category == typeof(CustomerJwtSessionValidator).FullName
                && entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Missing_customer_is_http_401()
    {
        var (db, tenant, _) = await SeedAsync();
        await using var _ = db;
        var logs = new SecretLogCapture();
        using var host = await StartAsync(logs, db);
        var token = TokenFor(Guid.NewGuid(), tenant.Id);

        var response = await SendAsync(host, token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("An unexpected error occurred.", body, StringComparison.Ordinal);
        AssertNoSecrets(logs, body, token, MemberEmail, MemberPhone);
    }

    [Fact]
    public async Task Jwt_tenant_is_used_when_the_ambient_tenant_differs()
    {
        var (db, _, customer) = await SeedAsync(ambientTenantId: Guid.NewGuid());
        await using var _ = db;
        var logs = new SecretLogCapture();
        using var host = await StartAsync(logs, db);

        var response = await SendAsync(host, Issue(customer));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Token_for_another_tenant_is_http_401()
    {
        var (db, _, customer) = await SeedAsync();
        await using var _ = db;
        var logs = new SecretLogCapture();
        using var host = await StartAsync(logs, db);
        var token = TokenFor(customer.Id, Guid.NewGuid());

        var response = await SendAsync(host, token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertNoSecrets(logs, await response.Content.ReadAsStringAsync(), token, MemberEmail, MemberPhone);
    }

    [Fact]
    public async Task Store_failure_is_http_500_and_logs_omit_secrets()
    {
        var token = TokenFor(Guid.NewGuid(), Guid.NewGuid());
        var failure = new InvalidOperationException($"{Canary} token={token} email={MemberEmail} phone={MemberPhone}");
        var logs = new SecretLogCapture();
        using var host = await StartAsync(
            logs,
            validator: provider => new CustomerJwtSessionValidator(
                (_, _, _) => throw failure,
                provider.GetRequiredService<ILogger<CustomerJwtSessionValidator>>()));

        var response = await SendAsync(host, token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("An unexpected error occurred.", json.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain("Session expired", body, StringComparison.Ordinal);
        AssertNoSecrets(
            logs,
            body + "\n" + response.Headers.WwwAuthenticate,
            token,
            MemberEmail,
            MemberPhone,
            Canary);
        Assert.Contains(
            logs.Entries,
            entry => entry.Message.Contains("ExceptionType=System.InvalidOperationException", StringComparison.Ordinal)
                && entry.ExceptionText?.Contains(Canary, StringComparison.Ordinal) != true);
    }

    [Fact]
    public async Task Disposed_store_is_http_500()
    {
        var (db, _, customer) = await SeedAsync();
        var token = Issue(customer);
        await db.DisposeAsync();
        var logs = new SecretLogCapture();
        using var host = await StartAsync(logs, db);

        var response = await SendAsync(host, token);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("An unexpected error occurred.", json.RootElement.GetProperty("error").GetString());
        AssertNoSecrets(
            logs,
            body,
            token,
            MemberEmail,
            MemberPhone,
            customer.Id.ToString(),
            "Cannot access a disposed context");
    }

    [Fact]
    public void Stamp_lookup_constrains_customer_and_tenant()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new AppDbContext(options, new FakeTenantProvider { TenantId = Guid.NewGuid() });
        var customerId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var sql = CustomerJwtSessionValidator.Lookup(db, customerId, tenantId).ToQueryString();

        Assert.Contains("tenant_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@tenantId", sql, StringComparison.Ordinal);
        Assert.Contains("@customerId", sql, StringComparison.Ordinal);
        Assert.Contains("customers", sql, StringComparison.OrdinalIgnoreCase);
        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(statements);
    }

    private static void AssertStoreFailureIsSafe(CustomerSessionStoreUnavailableException exception, ListLogger logger)
    {
        Assert.Null(exception.InnerException);
        Assert.IsNotAssignableFrom<UnauthorizedAccessException>(exception);
        Assert.DoesNotContain(Canary, exception.ToString(), StringComparison.Ordinal);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Contains("Customer session stamp check failed.", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, entry.Message, StringComparison.Ordinal);
    }

    private static async Task<(AppDbContext Db, Tenant Tenant, Customer Customer)> SeedAsync(Guid? ambientTenantId = null)
    {
        var tenant = new Tenant("Session Club", "66666666000191", subdomain: "sessionclub");
        var db = InMemoryAppDb.Create(new FakeTenantProvider
        {
            TenantId = ambientTenantId ?? tenant.Id,
        });
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Membro",
            Email = MemberEmail,
            Phone = MemberPhone,
            PasswordHash = "hash",
            EmailVerifiedAt = DateTimeOffset.Parse("2026-10-15T00:00:00+00:00"),
        };
        db.Tenants.Add(tenant);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return (db, tenant, customer);
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:Url"] = "https://example.supabase.co",
                ["Supabase:JwtSecret"] = JwtSecret,
            })
            .Build();

    private static string Issue(Customer customer) => new CustomerJwtIssuer(Config()).IssueToken(customer);

    private static string TokenFor(Guid customerId, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, customerId.ToString()),
            new(CustomerClaimTypes.CustomerId, customerId.ToString()),
            new(CustomerClaimTypes.TenantId, tenantId.ToString()),
            new(CustomerClaimTypes.Role, AuthRoles.Customer),
            new(ClaimTypes.Role, AuthRoles.Customer),
            new(JwtRegisteredClaimNames.Email, MemberEmail),
            new("phone", MemberPhone),
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSecret));
        var token = new JwtSecurityToken(
            issuer: CustomerJwtIssuer.Issuer,
            audience: CustomerJwtIssuer.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static ClaimsPrincipal Principal(Guid customerId, Guid tenantId, string? stamp = null)
    {
        var claims = new List<Claim>
        {
            new(CustomerClaimTypes.CustomerId, customerId.ToString()),
            new(CustomerClaimTypes.TenantId, tenantId.ToString()),
        };
        if (stamp is not null)
        {
            claims.Add(new Claim(CustomerPasswordStamp.ClaimType, stamp));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "CustomerJwt"));
    }

    private static TokenValidatedContext Context(ClaimsPrincipal? principal, CancellationToken requestAborted = default)
    {
        var http = new DefaultHttpContext();
        if (requestAborted != CancellationToken.None)
        {
            http.RequestAborted = requestAborted;
        }

        return new TokenValidatedContext(
            http,
            new AuthenticationScheme(
                CustomerJwtBearerDefaults.AuthenticationScheme,
                displayName: null,
                typeof(JwtBearerHandler)),
            new JwtBearerOptions())
        {
            Principal = principal,
        };
    }

    private static Task<IHost> StartAsync(
        SecretLogCapture logs,
        AppDbContext? database = null,
        Func<IServiceProvider, CustomerJwtSessionValidator>? validator = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(logging =>
                    {
                        logging.ClearProviders();
                        logging.AddProvider(logs);
                        logging.SetMinimumLevel(LogLevel.Trace);
                    });
                    if (database is not null)
                    {
                        services.AddSingleton(database);
                    }

                    services.AddSupabaseAuthentication(Config());
                    if (validator is not null)
                    {
                        services.AddScoped(validator);
                    }

                    services.AddControllers()
                        .ConfigureApplicationPartManager(manager =>
                        {
                            manager.ApplicationParts.Clear();
                            foreach (var provider in manager.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                            {
                                manager.FeatureProviders.Remove(provider);
                            }

                            manager.ApplicationParts.Add(new AssemblyPart(typeof(CustomerSessionProbeController).Assembly));
                            manager.FeatureProviders.Add(
                                new SessionControllerFeatureProvider(typeof(CustomerSessionProbeController)));
                        });
                });
                web.Configure(app =>
                {
                    app.UsePlatformExceptionHandler();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

        return Task.FromResult(host);
    }

    private static async Task<HttpResponseMessage> SendAsync(IHost host, string token)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync("/api/test/customer-session");
    }

    private static void AssertNoSecrets(SecretLogCapture logs, string exposedText, params string[] secrets)
    {
        var text = logs.AllText + "\n" + exposedText;
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    private sealed class ListLogger : ILogger<CustomerJwtSessionValidator>
    {
        public List<Entry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new Entry(logLevel, formatter(state, exception), exception));
        }

        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);
    }

    private sealed class SecretLogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<Captured> _entries = new();

        public IReadOnlyList<Captured> Entries => _entries.ToArray();

        public string AllText => string.Join(
            '\n',
            _entries.Select(entry => $"{entry.Category}\n{entry.Message}\n{entry.ExceptionText}"));

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        public sealed record Captured(LogLevel Level, string Category, string Message, string? ExceptionText);

        private sealed class CapturingLogger(string categoryName, ConcurrentQueue<Captured> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(new Captured(
                    logLevel,
                    categoryName,
                    formatter(state, exception),
                    exception?.ToString()));
            }
        }
    }
}
