using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Core.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Platform.Api.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class CustomerLoginThrottleTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture postgres;
    public CustomerLoginThrottleTests(PostgresContainerFixture postgres) => this.postgres = postgres;

    [DockerFact]
    public async Task Email_limit_is_normalized_and_isolated_by_tenant()
    {
        var tenant = Guid.NewGuid();
        Assert.Equal(0, await Consume(tenant, " User@club.test ", "192.0.2.1"));
        Assert.Equal(0, await Consume(tenant, "user@club.test", "192.0.2.2"));
        Assert.InRange(await Consume(tenant, "USER@club.test", "192.0.2.3"), 1, 900);
        Assert.Equal(0, await Consume(Guid.NewGuid(), "user@club.test", "192.0.2.3"));
    }

    [DockerFact]
    public async Task Ip_limit_covers_different_emails_and_tenants()
    {
        var ip = Guid.NewGuid().ToString();
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, await Consume(Guid.NewGuid(), $"{i}@club.test", ip));
        Assert.InRange(await Consume(Guid.NewGuid(), "other@club.test", ip), 1, 900);
    }

    [DockerFact]
    public async Task Blocked_ip_cannot_consume_victim_email_budget()
    {
        var attackerIp = Guid.NewGuid().ToString();
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, await Consume(Guid.NewGuid(), $"{i}@club.test", attackerIp));

        var victimTenant = Guid.NewGuid();
        for (var i = 0; i < 12; i++)
            Assert.InRange(await Consume(victimTenant, "victim@club.test", attackerIp), 1, 900);

        var victimIp = Guid.NewGuid().ToString();
        Assert.Equal(0, await Consume(victimTenant, "victim@club.test", victimIp));
        Assert.Equal(0, await Consume(victimTenant, "victim@club.test", victimIp));
        Assert.InRange(await Consume(victimTenant, "victim@club.test", victimIp), 1, 900);
    }

    [DockerFact]
    public async Task Successful_attempts_do_not_create_or_increment_ip_bucket()
    {
        var ip = Guid.NewGuid().ToString();
        for (var i = 0; i < 6; i++)
            Assert.Equal(0, await Consume(Guid.NewGuid(), $"success{i}@club.test", ip, failed: false));
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, await Consume(Guid.NewGuid(), $"failure{i}@club.test", ip));

        var tenant = Guid.NewGuid();
        Assert.InRange(await Consume(tenant, "valid@club.test", ip, failed: false), 1, 900);
        Assert.Equal(0, await Consume(tenant, "valid@club.test", Guid.NewGuid().ToString()));
        Assert.Equal(0, await Consume(tenant, "valid@club.test", Guid.NewGuid().ToString()));
    }

    [DockerFact]
    public async Task Preliminary_check_does_not_consume_budgets_and_detects_exhaustion()
    {
        Assert.NotNull(postgres.Factory);
        await using var db = postgres.Factory.Create(new FakeTenantProvider());
        var gate = new CustomerLoginThrottle(db, CustomerAuthHarness.CreateConfig(),
            Options.Create(new CustomerLoginThrottleOptions { EmailLimit = 2, IpLimit = 3 }));
        var tenant = Guid.NewGuid();
        var ip = Guid.NewGuid().ToString();
        for (var i = 0; i < 6; i++)
            Assert.Equal(0, await gate.CheckAsync(tenant, "check@club.test", ip, default));
        Assert.Equal(0, await Consume(tenant, "check@club.test", ip));
        Assert.Equal(0, await Consume(tenant, "check@club.test", ip));
        Assert.InRange(await gate.CheckAsync(tenant, "check@club.test", Guid.NewGuid().ToString(), default), 1, 900);
        Assert.Equal(0, await Consume(Guid.NewGuid(), "third@club.test", ip));
        Assert.InRange(await gate.CheckAsync(Guid.NewGuid(), "other@club.test", ip, default), 1, 900);
    }

    [DockerFact]
    public async Task Parallel_instances_admit_exactly_email_budget()
    {
        var tenant = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => Consume(tenant, "parallel@club.test", $"ip-{tenant}-{i}")));
        Assert.Equal(2, results.Count(r => r == 0));
        Assert.All(results.Where(r => r != 0), r => Assert.InRange(r, 1, 900));
    }

    [DockerFact]
    public async Task Parallel_instances_admit_exactly_ip_budget()
    {
        var ip = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => Consume(Guid.NewGuid(), $"{i}@club.test", ip)));
        Assert.Equal(3, results.Count(r => r == 0));
    }

    [Fact]
    public async Task Login_returns_429_with_retry_after()
    {
        var controller = CustomerAuthHarness.CreateController(new StubCustomerAuthService
        {
            ToThrow = new CustomerLoginRateLimitedException(42),
        });
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var result = await controller.Login(new CustomerLoginRequestDto
        {
            Email = "someone@club.test", Password = "wrong",
        }, "authclub", CancellationToken.None);
        var response = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(429, response.StatusCode);
        Assert.Equal("42", controller.Response.Headers.RetryAfter.ToString());
    }

    [Fact]
    public async Task Already_blocked_login_is_rejected_before_password_work()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await harness.Auth.RegisterAsync(CustomerAuthHarness.NewRegister(email: "blocked@club.test"), default);
        var customer = await harness.Db.Customers.SingleAsync();
        customer.PasswordHash = "invalid-base64-hash";
        await harness.Db.SaveChangesAsync();
        var auth = CustomerAuthHarness.CreateService(harness.Db, harness.TenantProvider,
            harness.Phone, harness.Email, new AllowAllPhoneVerificationSendGate(), TimeProvider.System,
            loginThrottle: new BlockedLoginThrottle());

        var error = await Assert.ThrowsAsync<CustomerLoginRateLimitedException>(() => auth.LoginAsync(
            new CustomerLoginRequestDto { Email = customer.Email!, Password = "wrong" }, default));
        Assert.Equal(42, error.RetryAfterSeconds);
    }

    [Fact]
    public async Task Missing_and_existing_email_have_identical_invalid_credentials_response()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await harness.Auth.RegisterAsync(CustomerAuthHarness.NewRegister(email: "known@club.test"), default);
        var known = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.Auth.LoginAsync(
            new CustomerLoginRequestDto { Email = "known@club.test", Password = "wrong" }, default));
        var missing = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.Auth.LoginAsync(
            new CustomerLoginRequestDto { Email = "missing@club.test", Password = "wrong" }, default));
        Assert.Equal(known.Message, missing.Message);
    }

    [Fact]
    public async Task Missing_email_performs_comparable_password_work()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await harness.Auth.RegisterAsync(CustomerAuthHarness.NewRegister(email: "timing@club.test"), default);
        async Task Guess(string email) => await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Auth.LoginAsync(new CustomerLoginRequestDto { Email = email, Password = "wrong" }, default));
        await Guess("timing@club.test");
        await Guess("absent@club.test");
        double known = 0, missing = 0;
        for (var i = 0; i < 8; i++)
        {
            var timer = Stopwatch.StartNew();
            await Guess("timing@club.test");
            known += timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            await Guess("absent@club.test");
            missing += timer.Elapsed.TotalMilliseconds;
        }
        // Detects skipping the KDF; broad tolerance accommodates shared CI runners.
        Assert.InRange(missing / known, 0.45, 2.2);
    }

    [DockerFact]
    public async Task Existing_and_missing_accounts_receive_same_throttle_response()
    {
        var known = await LoginScenario(accountExists: true);
        var missing = await LoginScenario(accountExists: false);
        Assert.Equal(known.Body, missing.Body);
        Assert.InRange(known.RetryAfter, 1, 900);
        Assert.InRange(missing.RetryAfter, 1, 900);
    }

    [DockerFact]
    public async Task Successful_logins_neither_increment_nor_reset_ip_failure_budget()
    {
        Assert.NotNull(postgres.Factory);
        var tenant = new Tenant("Login", "login" + Guid.NewGuid().ToString("N")[..14]);
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = postgres.Factory.Create(provider);
        db.Tenants.Add(tenant);
        var customer = new Customer
        {
            TenantId = tenant.Id, Name = "Customer", Email = "success@club.test",
            EmailVerifiedAt = DateTimeOffset.UtcNow,
        };
        customer.PasswordHash = new PasswordHasher<Customer>().HashPassword(customer, "secret123");
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var gate = new CustomerLoginThrottle(db, CustomerAuthHarness.CreateConfig(),
            Options.Create(new CustomerLoginThrottleOptions { EmailLimit = 10, IpLimit = 2 }));
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.99");
        // Seed the same canonical IP bucket the service will consume.
        Assert.Equal(0, await gate.ConsumeAsync(tenant.Id, "wrong@club.test", "192.0.2.99", true, default));
        var auth = CustomerAuthHarness.CreateService(db, provider, new FakePhoneVerificationClient(),
            new FakeEmailProvider(), new AllowAllPhoneVerificationSendGate(), TimeProvider.System,
            new HttpContextAccessor { HttpContext = http }, loginThrottle: gate);
        for (var i = 0; i < 3; i++)
            Assert.Equal("test-token", (await auth.LoginAsync(
                new CustomerLoginRequestDto { Email = customer.Email, Password = "secret123" }, default)).Token);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.LoginAsync(
            new CustomerLoginRequestDto { Email = "second-failure@club.test", Password = "wrong" }, default));
        await Assert.ThrowsAsync<CustomerLoginRateLimitedException>(() => auth.LoginAsync(
            new CustomerLoginRequestDto { Email = "another@club.test", Password = "wrong" }, default));
    }

    [DockerFact]
    public async Task Expired_window_reopens_without_sliding_on_rejected_attempts()
    {
        var tenant = Guid.NewGuid();
        var email = "expiry@club.test";
        var ip = Guid.NewGuid().ToString();
        Assert.Equal(0, await Consume(tenant, email, ip));
        Assert.Equal(0, await Consume(tenant, email, ip));
        Assert.True(await Consume(tenant, email, ip) > 0);
        Assert.NotNull(postgres.Factory);
        await using var db = postgres.Factory.Create(new FakeTenantProvider());
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE core.customer_login_throttle SET expires_at = statement_timestamp() + interval '45 seconds'");
        Assert.InRange(await Consume(tenant, email, ip), 1, 45);
        // Operate on an isolated test database; no wall-clock sleeps.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE core.customer_login_throttle SET expires_at = statement_timestamp() - interval '1 second'");
        Assert.Equal(0, await Consume(tenant, email, ip));
    }

    [DockerFact]
    public async Task Retention_removes_expired_buckets_and_preserves_active_limits()
    {
        var tenant = Guid.NewGuid();
        var ip = Guid.NewGuid().ToString();
        await Consume(tenant, "retention@club.test", ip);
        Assert.NotNull(postgres.Factory);
        await using var db = postgres.Factory.Create(new FakeTenantProvider());
        // Only the sentinel is expired; the real email/IP buckets must remain blocked.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO core.customer_login_throttle VALUES ('expired-test', 1, statement_timestamp() - interval '1 day')");
        await new DataRetentionJob(db, NullLogger<DataRetentionJob>.Instance).ExecuteAsync(default);
        var remaining = await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM core.customer_login_throttle WHERE bucket_key = 'expired-test'").SingleAsync();
        Assert.Equal(0, remaining);
        Assert.Equal(0, await Consume(tenant, "retention@club.test", ip));
        Assert.True(await Consume(tenant, "retention@club.test", ip) > 0);
    }

    [DockerFact]
    public async Task Retention_skips_locked_buckets_without_blocking_login_writes()
    {
        var tenant = Guid.NewGuid();
        var ip = Guid.NewGuid().ToString();
        await Consume(tenant, "locked@club.test", ip);
        Assert.NotNull(postgres.Factory);
        await using var writer = postgres.Factory.Create(new FakeTenantProvider());
        await writer.Database.ExecuteSqlRawAsync(
            "UPDATE core.customer_login_throttle SET expires_at = statement_timestamp() - interval '1 second'");
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlRawAsync("SELECT bucket_key FROM core.customer_login_throttle FOR UPDATE");
        // Simulate a login renewing expired buckets while retention runs in another instance.
        var gate = new CustomerLoginThrottle(writer, CustomerAuthHarness.CreateConfig(),
            Options.Create(new CustomerLoginThrottleOptions { EmailLimit = 2, IpLimit = 3 }));
        Assert.Equal(0, await gate.ConsumeAsync(tenant, "locked@club.test", ip, true, default));
        await using var cleaner = postgres.Factory.Create(new FakeTenantProvider());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await new DataRetentionJob(cleaner, NullLogger<DataRetentionJob>.Instance).ExecuteAsync(deadline.Token);
        await transaction.CommitAsync();
        Assert.Equal(0, await Consume(tenant, "locked@club.test", ip));
        Assert.True(await Consume(tenant, "locked@club.test", ip) > 0);
    }

    [DockerFact]
    public async Task Migration_down_removes_only_throttle_and_up_recreates_it()
    {
        Assert.NotNull(postgres.Factory);
        await using var db = postgres.Factory.Create(new FakeTenantProvider());
        var migrator = db.GetService<IMigrator>();
        const string migration = "20261023120000_AddCustomerLoginThrottle";
        var previous = db.Database.GetMigrations().TakeWhile(id => id != migration).Last();
        var tenants = await db.Tenants.IgnoreQueryFilters().CountAsync();
        try
        {
            await migrator.MigrateAsync(previous);
            Assert.True(await db.Database.SqlQueryRaw<bool>(
                "SELECT to_regclass('core.customer_login_throttle') IS NULL AS \"Value\"").SingleAsync());
        }
        finally
        {
            await migrator.MigrateAsync(migration);
        }
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            "SELECT to_regclass('core.customer_login_throttle') IS NULL AS \"Value\"").SingleAsync());
        Assert.Equal(tenants, await db.Tenants.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await Consume(Guid.NewGuid(), "after-migration@club.test", Guid.NewGuid().ToString()));
    }

    private async Task<(string Body, int RetryAfter)> LoginScenario(bool accountExists)
    {
        Assert.NotNull(postgres.Factory);
        var tenant = new Tenant("Login", "login" + Guid.NewGuid().ToString("N")[..14]);
        var provider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = postgres.Factory.Create(provider);
        db.Tenants.Add(tenant);
        if (accountExists)
        {
            var customer = new Customer { TenantId = tenant.Id, Name = "Customer", Email = "login@club.test" };
            customer.PasswordHash = new PasswordHasher<Customer>().HashPassword(customer, "secret123");
            db.Customers.Add(customer);
        }
        await db.SaveChangesAsync();
        var gate = new CustomerLoginThrottle(db, CustomerAuthHarness.CreateConfig(),
            Options.Create(new CustomerLoginThrottleOptions { EmailLimit = 2, IpLimit = 30 }));
        var auth = CustomerAuthHarness.CreateService(db, provider, new FakePhoneVerificationClient(),
            new FakeEmailProvider(), new AllowAllPhoneVerificationSendGate(), TimeProvider.System,
            loginThrottle: gate);
        var controller = CustomerAuthHarness.CreateController(auth);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        var request = new CustomerLoginRequestDto { Email = " LOGIN@club.test ", Password = "wrong" };
        for (var i = 0; i < 2; i++)
            Assert.IsType<UnauthorizedObjectResult>((await controller.Login(request, "authclub", default)).Result);
        var result = Assert.IsType<ObjectResult>((await controller.Login(request, "authclub", default)).Result);
        Assert.Equal(429, result.StatusCode);
        return (JsonSerializer.Serialize(result.Value), int.Parse(controller.Response.Headers.RetryAfter.ToString()));
    }

    private async Task<int> Consume(Guid tenant, string email, string ip, bool failed = true)
    {
        Assert.NotNull(postgres.Factory);
        await using var db = postgres.Factory.Create(new FakeTenantProvider());
        var gate = new CustomerLoginThrottle(db, CustomerAuthHarness.CreateConfig(),
            Options.Create(new CustomerLoginThrottleOptions { EmailLimit = 2, IpLimit = 3 }));
        return await gate.ConsumeAsync(tenant, email, ip, failed, default);
    }
}

internal sealed class BlockedLoginThrottle : ICustomerLoginThrottle
{
    public Task<int> CheckAsync(Guid tenantId, string normalizedEmail, string? clientIp,
        CancellationToken cancellationToken) => Task.FromResult(42);

    public Task<int> ConsumeAsync(Guid tenantId, string normalizedEmail, string? clientIp,
        bool failed, CancellationToken cancellationToken) => Task.FromResult(42);
}
