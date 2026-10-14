using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Modules.CustomerAuth.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class PasswordResetTests
{
    [Fact]
    public async Task Unknown_email_returns_the_generic_message_and_sends_nothing()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var controller = CustomerAuthHarness.CreateController(harness.Auth);

        var missing = await controller.RequestPasswordReset(
            new PasswordResetRequestDto { Email = "missing@club.test" },
            "authclub",
            CancellationToken.None);
        Assert.Equal(0, harness.Email.SendCount);
        var knownCustomer = await RegisterVerifiedAsync(harness, "ana@club.test");
        harness.Email.Sent.Clear();
        var known = await controller.RequestPasswordReset(
            new PasswordResetRequestDto { Email = "ANA@club.test" },
            "authclub",
            CancellationToken.None);

        var missingOk = Assert.IsType<OkObjectResult>(missing.Result);
        var knownOk = Assert.IsType<OkObjectResult>(known.Result);
        var missingBody = Assert.IsType<PasswordResetRequestResponseDto>(missingOk.Value);
        var knownBody = Assert.IsType<PasswordResetRequestResponseDto>(knownOk.Value);
        Assert.Equal(CustomerAuthService.PasswordResetGenericMessage, missingBody.Message);
        Assert.Equal(missingBody, knownBody);
        Assert.Equal(1, harness.Email.SendCount);
        Assert.Equal(RolvixEmailLayout.PasswordResetSubject, harness.Email.Sent[0].Subject);
        _ = knownCustomer;
    }

    [Fact]
    public async Task Known_email_stores_only_the_hash_and_sends_the_reset_email()
    {
        var issuedAt = DateTimeOffset.Parse("2026-10-12T15:00:00Z");
        var time = new TestTimeProvider(issuedAt);
        await using var harness = await CustomerAuthHarness.CreateAsync(timeProvider: time);
        await RegisterVerifiedAsync(harness, "ana@club.test");
        harness.Email.Sent.Clear();

        await harness.Auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = "ana@club.test" },
            CancellationToken.None);

        var sent = Assert.Single(harness.Email.Sent);
        Assert.Equal("ana@club.test", sent.Recipient);
        Assert.Equal(RolvixEmailLayout.PasswordResetSubject, sent.Subject);
        Assert.Contains("Este código expira em 15 minutos.", sent.Body, StringComparison.Ordinal);
        var code = FakeEmailProvider.ExtractSixDigitCode(sent.Body);
        var otp = await harness.Db.OtpCodes.SingleAsync(row => row.Purpose == OtpPurposes.PasswordReset);
        Assert.Null(otp.Code);
        Assert.False(string.IsNullOrWhiteSpace(otp.CodeHash));
        Assert.Equal(issuedAt.Add(CustomerVerificationCodeService.PasswordResetTimeToLive), otp.ExpiresAt);
        Assert.NotEqual(code, otp.CodeHash);
    }

    [Fact]
    public async Task Expired_code_does_not_change_the_password()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-10-12T15:00:00Z"));
        await using var harness = await CustomerAuthHarness.CreateAsync(timeProvider: time);
        var customer = await RegisterVerifiedAsync(harness, "expired@club.test");
        var code = await RequestResetCodeAsync(harness, "expired@club.test");
        var hashBefore = customer.PasswordHash;
        time.Advance(TimeSpan.FromMinutes(15));

        var ex = await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.ConfirmPasswordResetAsync(
                new PasswordResetConfirmRequestDto
                {
                    Email = "expired@club.test",
                    Code = code,
                    NewPassword = "new-secret",
                },
                CancellationToken.None));

        Assert.Equal(TwilioVerifyPhoneVerificationClient.InvalidOrExpiredMessage, ex.Message);
        Assert.Equal(hashBefore, customer.PasswordHash);
        Assert.Null(customer.PasswordChangedAt);
    }

    [Fact]
    public async Task Wrong_code_increments_attempts_and_keeps_the_password()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var customer = await RegisterVerifiedAsync(harness, "wrong@club.test");
        var code = await RequestResetCodeAsync(harness, "wrong@club.test");
        var hashBefore = customer.PasswordHash;
        var wrong = code == "000000" ? "000001" : "000000";

        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.ConfirmPasswordResetAsync(
                new PasswordResetConfirmRequestDto
                {
                    Email = "wrong@club.test",
                    Code = wrong,
                    NewPassword = "new-secret",
                },
                CancellationToken.None));

        var otp = await harness.Db.OtpCodes.SingleAsync(row => row.Purpose == OtpPurposes.PasswordReset);
        Assert.Equal(1, otp.Attempts);
        Assert.False(otp.IsUsed);
        Assert.Equal(hashBefore, customer.PasswordHash);
    }

    [Fact]
    public async Task Five_failed_attempts_then_the_correct_code_is_rejected()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var customer = await RegisterVerifiedAsync(harness, "lock@club.test");
        var code = await RequestResetCodeAsync(harness, "lock@club.test");
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < CustomerVerificationCodeService.MaxAttempts; i++)
        {
            await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
                () => harness.Auth.ConfirmPasswordResetAsync(
                    new PasswordResetConfirmRequestDto
                    {
                        Email = "lock@club.test",
                        Code = wrong,
                        NewPassword = "new-secret",
                    },
                    CancellationToken.None));
        }

        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.ConfirmPasswordResetAsync(
                new PasswordResetConfirmRequestDto
                {
                    Email = "lock@club.test",
                    Code = code,
                    NewPassword = "new-secret",
                },
                CancellationToken.None));

        Assert.Null(customer.PasswordChangedAt);
        var login = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Auth.LoginAsync(
                new CustomerLoginRequestDto { Email = "lock@club.test", Password = "new-secret" },
                CancellationToken.None));
        Assert.Equal("Invalid email or password.", login.Message);
    }

    [Fact]
    public async Task Code_from_another_tenant_does_not_reset_this_tenant()
    {
        var databaseName = $"reset-iso-{Guid.NewGuid():N}";
        var tenantA = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantB = new Tenant("Club B", "11222333000181", subdomain: "club-b");
        var tenantProvider = new FakeTenantProvider { TenantId = tenantA.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider, databaseName);
        db.Tenants.Add(tenantA);
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        var emailProvider = new FakeEmailProvider();
        var auth = CustomerAuthHarness.CreateService(
            db,
            tenantProvider,
            new FakePhoneVerificationClient(),
            emailProvider,
            new AllowAllPhoneVerificationSendGate(),
            TimeProvider.System);
        var register = CustomerAuthHarness.NewRegister(
            email: "shared@club.test",
            phone: "+5511933331111");
        await auth.RegisterAsync(register, CancellationToken.None);

        tenantProvider.TenantId = tenantB.Id;
        await auth.RegisterAsync(register, CancellationToken.None);

        tenantProvider.TenantId = tenantA.Id;
        await auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = "shared@club.test" },
            CancellationToken.None);
        var codeA = FakeEmailProvider.ExtractSixDigitCode(emailProvider.Sent[^1].Body);
        var hashB = (await db.Customers.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenantB.Id)).PasswordHash;

        tenantProvider.TenantId = tenantB.Id;
        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => auth.ConfirmPasswordResetAsync(
                new PasswordResetConfirmRequestDto
                {
                    Email = "shared@club.test",
                    Code = codeA,
                    NewPassword = "new-secret",
                },
                CancellationToken.None));

        var customerB = await db.Customers.IgnoreQueryFilters().SingleAsync(c => c.TenantId == tenantB.Id);
        Assert.Equal(hashB, customerB.PasswordHash);
        Assert.Null(customerB.PasswordChangedAt);
        Assert.Null(customerB.EmailVerifiedAt);
    }

    [Fact]
    public async Task Successful_reset_of_an_unverified_account_confirms_email_and_old_password_fails()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: "pending@club.test"),
            CancellationToken.None);
        var code = await RequestResetCodeAsync(harness, "pending@club.test");

        await harness.Auth.ConfirmPasswordResetAsync(
            new PasswordResetConfirmRequestDto
            {
                Email = "pending@club.test",
                Code = code,
                NewPassword = "new-secret",
            },
            CancellationToken.None);

        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == "pending@club.test");
        Assert.NotNull(customer.EmailVerifiedAt);
        Assert.NotNull(customer.PasswordChangedAt);
        Assert.Equal(customer.PasswordChangedAt, customer.EmailVerifiedAt);
        Assert.Equal(0, customer.PasswordChangedAt!.Value.Ticks % TimeSpan.TicksPerMillisecond);

        var auth = await harness.Auth.LoginAsync(
            new CustomerLoginRequestDto { Email = "pending@club.test", Password = "new-secret" },
            CancellationToken.None);
        Assert.True(auth.Customer.EmailVerified);

        var oldPassword = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Auth.LoginAsync(
                new CustomerLoginRequestDto { Email = "pending@club.test", Password = "secret123" },
                CancellationToken.None));
        Assert.Equal("Invalid email or password.", oldPassword.Message);
    }

    [Fact]
    public async Task Short_password_is_rejected_and_the_code_stays_usable()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await RegisterVerifiedAsync(harness, "short@club.test");
        var code = await RequestResetCodeAsync(harness, "short@club.test");
        var controller = CustomerAuthHarness.CreateController(harness.Auth);

        var result = await controller.ConfirmPasswordReset(
            new PasswordResetConfirmRequestDto
            {
                Email = "short@club.test",
                Code = code,
                NewPassword = "short",
            },
            "authclub",
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("at least 8", $"{badRequest.Value}", StringComparison.Ordinal);

        await harness.Auth.ConfirmPasswordResetAsync(
            new PasswordResetConfirmRequestDto
            {
                Email = "short@club.test",
                Code = code,
                NewPassword = "long-enough",
            },
            CancellationToken.None);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == "short@club.test");
        Assert.NotNull(customer.PasswordChangedAt);
    }

    [Fact]
    public async Task Email_verification_code_cannot_reset_the_password()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: "purpose@club.test"),
            CancellationToken.None);
        var verificationCode = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);

        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.ConfirmPasswordResetAsync(
                new PasswordResetConfirmRequestDto
                {
                    Email = "purpose@club.test",
                    Code = verificationCode,
                    NewPassword = "new-secret",
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task Cooldown_does_not_send_a_second_reset_email()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-10-12T15:00:00Z"));
        var gate = new PhoneVerificationSendGate(new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()), time);
        await using var harness = await CustomerAuthHarness.CreateAsync(sendGate: gate, timeProvider: time);
        await RegisterVerifiedAsync(harness, "cool@club.test");
        harness.Email.Sent.Clear();

        await harness.Auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = "cool@club.test" },
            CancellationToken.None);
        await harness.Auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = "cool@club.test" },
            CancellationToken.None);
        Assert.Equal(1, harness.Email.SendCount);

        time.Advance(TimeSpan.FromSeconds(46));
        await harness.Auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = "cool@club.test" },
            CancellationToken.None);
        Assert.Equal(2, harness.Email.SendCount);
    }

    [Fact]
    public async Task Ip_limit_returns_429_and_verification_limit_does_not_block_reset()
    {
        var openCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var gate = new PhoneVerificationSendGate(openCache, TimeProvider.System);
        await using var open = await CustomerAuthHarness.CreateAsync(sendGate: gate);
        await RegisterVerifiedAsync(open, "separate@club.test");
        open.Email.Sent.Clear();
        FillScope(gate, IPhoneVerificationSendGate.VerificationScope);
        await open.Auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = "separate@club.test" },
            CancellationToken.None);
        Assert.Equal(1, open.Email.SendCount);

        var limitedCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var limitedGate = new PhoneVerificationSendGate(limitedCache, TimeProvider.System);
        await using var limited = await CustomerAuthHarness.CreateAsync(sendGate: limitedGate);
        await RegisterVerifiedAsync(limited, "limited@club.test");
        limited.Email.Sent.Clear();
        FillScope(limitedGate, IPhoneVerificationSendGate.PasswordResetScope);
        var controller = CustomerAuthHarness.CreateController(limited.Auth);

        var result = await controller.RequestPasswordReset(
            new PasswordResetRequestDto { Email = "limited@club.test" },
            "authclub",
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
        Assert.Equal(0, limited.Email.SendCount);
    }

    [Fact]
    public async Task Eleven_fast_requests_match_for_known_and_unknown_email()
    {
        var known = await CustomerBurstAsync(registerKnownCustomer: true);
        var unknown = await CustomerBurstAsync(registerKnownCustomer: false);

        Assert.Equal(known.Statuses, unknown.Statuses);
        Assert.Equal(PhoneVerificationSendGate.IpMaxAttempts + 1, known.Statuses.Count);
        Assert.All(
            known.Statuses.Take(PhoneVerificationSendGate.IpMaxAttempts),
            status => Assert.Equal(StatusCodes.Status200OK, status));
        Assert.Equal(StatusCodes.Status429TooManyRequests, known.Statuses[^1]);
        Assert.Equal(1, known.ResetEmails);
        Assert.Equal(0, unknown.ResetEmails);
    }

    private static async Task<CustomerBurst> CustomerBurstAsync(bool registerKnownCustomer)
    {
        var gate = new PhoneVerificationSendGate(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            TimeProvider.System);
        await using var harness = await CustomerAuthHarness.CreateAsync(sendGate: gate);
        var email = registerKnownCustomer ? "ana@club.test" : "missing@club.test";
        if (registerKnownCustomer)
        {
            await RegisterVerifiedAsync(harness, email);
            harness.Email.Sent.Clear();
        }

        var controller = CustomerAuthHarness.CreateController(harness.Auth);
        var statuses = new List<int>(PhoneVerificationSendGate.IpMaxAttempts + 1);
        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts + 1; i++)
        {
            var result = await controller.RequestPasswordReset(
                new PasswordResetRequestDto { Email = email },
                "authclub",
                CancellationToken.None);
            statuses.Add(StatusCodeOf(result));
        }

        return new CustomerBurst(statuses, harness.Email.SendCount);
    }

    private static int StatusCodeOf(ActionResult<PasswordResetRequestResponseDto> result) =>
        result.Result switch
        {
            OkObjectResult ok => ok.StatusCode ?? StatusCodes.Status200OK,
            ObjectResult obj => obj.StatusCode ?? StatusCodes.Status500InternalServerError,
            _ => throw new InvalidOperationException(
                $"Unexpected password-reset result {result.Result?.GetType().Name}."),
        };

    private sealed record CustomerBurst(IReadOnlyList<int> Statuses, int ResetEmails);

    [Fact]
    public async Task Reset_email_is_queued_and_the_provider_is_not_called_inline()
    {
        var databaseName = $"reset-queue-{Guid.NewGuid():N}";
        var tenant = new Tenant("Auth Club", "66666666000191", subdomain: "authclub");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider, databaseName);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var email = new FakeEmailProvider();
        var queue = new RecordingPasswordResetEmailQueue();
        var auth = CustomerAuthHarness.CreateService(
            db,
            tenantProvider,
            new FakePhoneVerificationClient(),
            email,
            new AllowAllPhoneVerificationSendGate(),
            TimeProvider.System,
            passwordResetEmails: queue);
        await auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: "queued@club.test"),
            CancellationToken.None);
        email.Sent.Clear();
        email.ThrowHttpRequestException = true;

        await auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = "queued@club.test" },
            CancellationToken.None);
        await auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = "missing@club.test" },
            CancellationToken.None);

        Assert.Equal(0, email.SendCount);
        var queued = Assert.Single(queue.Work);
        Assert.Equal("queued@club.test", queued.Recipient);
        Assert.Contains("Este código expira em 15 minutos.", RolvixEmailLayout.PasswordResetBody(queued.Code), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_failure_still_returns_the_generic_message()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await RegisterVerifiedAsync(harness, "mailfail@club.test");
        harness.Email.Sent.Clear();
        harness.Email.ThrowHttpRequestException = true;
        var controller = CustomerAuthHarness.CreateController(harness.Auth);

        var result = await controller.RequestPasswordReset(
            new PasswordResetRequestDto { Email = "mailfail@club.test" },
            "authclub",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<PasswordResetRequestResponseDto>(ok.Value);
        Assert.Equal(CustomerAuthService.PasswordResetGenericMessage, body.Message);
        Assert.Equal(0, harness.Email.SendCount);
    }

    [Fact]
    public async Task Reset_invalidates_the_previous_customer_jwt_and_the_new_login_token_works()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var customer = await RegisterVerifiedAsync(harness, "session@club.test");
        var issuer = new CustomerJwtIssuer(CustomerAuthHarness.CreateConfig());
        var previousToken = issuer.IssueToken(customer);
        Assert.DoesNotContain(
            CustomerPasswordStamp.ClaimType,
            new JwtSecurityTokenHandler().ReadJwtToken(previousToken).Claims.Select(claim => claim.Type),
            StringComparer.Ordinal);

        var code = await RequestResetCodeAsync(harness, "session@club.test");
        await harness.Auth.ConfirmPasswordResetAsync(
            new PasswordResetConfirmRequestDto
            {
                Email = "session@club.test",
                Code = code,
                NewPassword = "new-secret",
            },
            CancellationToken.None);

        var validator = new CustomerJwtSessionValidator(
            harness.Db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CustomerJwtSessionValidator>.Instance);
        Assert.False(await validator.IsCurrentAsync(PrincipalFrom(previousToken), CancellationToken.None));

        var refreshed = await harness.Db.Customers.SingleAsync(c => c.Email == "session@club.test");
        var nextToken = issuer.IssueToken(refreshed);
        var nextJwt = new JwtSecurityTokenHandler().ReadJwtToken(nextToken);
        Assert.Equal(
            CustomerPasswordStamp.Format(refreshed.PasswordChangedAt!.Value),
            nextJwt.Claims.Single(claim => claim.Type == CustomerPasswordStamp.ClaimType).Value);
        Assert.True(await validator.IsCurrentAsync(PrincipalFrom(nextToken), CancellationToken.None));

        using var host = await StartSessionHostAsync(harness.Db);
        var counter = host.Services.GetRequiredService<SessionQueryCounter>();
        counter.Count = 0;
        Assert.Equal(HttpStatusCode.Unauthorized, await GetSessionStatusAsync(host, previousToken));
        Assert.Equal(1, counter.Count);
        counter.Count = 0;
        Assert.Equal(HttpStatusCode.OK, await GetSessionStatusAsync(host, nextToken));
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public void Password_stamp_lookup_is_a_single_select()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=55432;Database=postgres;Username=postgres;Password=postgres")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new AppDbContext(options, new FakeTenantProvider());
        var sql = CustomerJwtSessionValidator
            .Lookup(db, Guid.NewGuid(), Guid.NewGuid())
            .ToQueryString();

        var statements = sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Single(statements);
        Assert.Contains("password_changed_at", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migration_adds_password_changed_at_without_touching_remote_hosts()
    {
        var migrationsDir = FindMigrationsDirectory();
        var files = Directory.GetFiles(migrationsDir, "*AddCustomerPasswordChangedAt.cs")
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Single(files);
        var sql = File.ReadAllText(files[0]);
        Assert.Contains("password_changed_at", sql, StringComparison.Ordinal);
        Assert.Contains("core", sql, StringComparison.Ordinal);
        Assert.Contains("customers", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("supabase.co", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Customer> RegisterVerifiedAsync(CustomerAuthHarness harness, string email)
    {
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[^1].Body);
        await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = code },
            CancellationToken.None);
        return await harness.Db.Customers.SingleAsync(c => c.Email == email);
    }

    private static async Task<string> RequestResetCodeAsync(CustomerAuthHarness harness, string email)
    {
        var before = harness.Email.SendCount;
        await harness.Auth.RequestPasswordResetAsync(
            new PasswordResetRequestDto { Email = email },
            CancellationToken.None);
        return FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[before].Body);
    }

    private static ClaimsPrincipal PrincipalFrom(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims, authenticationType: "CustomerJwt"));
    }

    private static void FillScope(PhoneVerificationSendGate gate, string scope)
    {
        var tenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            var decision = gate.Decide(tenantId, $"prefill{i}@club.test", clientIp: null, scope);
            if (decision == PhoneVerificationSendDecision.Limited)
            {
                return;
            }

            Assert.Equal(PhoneVerificationSendDecision.Send, decision);
        }
    }

    private static string FindMigrationsDirectory()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var migrations = Path.Combine(
                    dir.FullName,
                    "Core",
                    "Platform.Core.Infrastructure",
                    "Persistence",
                    "Migrations");
                if (Directory.Exists(migrations))
                {
                    return migrations;
                }

                dir = dir.Parent;
            }
        }

        throw new InvalidOperationException("Could not find the migrations directory.");
    }

    private static Task<IHost> StartSessionHostAsync(AppDbContext source)
    {
        var counter = new SessionQueryCounter();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:Url"] = "https://example.supabase.co",
                ["Supabase:JwtSecret"] = CustomerAuthHarness.JwtSecret,
            })
            .Build();

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton(counter);
                    services.AddSingleton(source);
                    services.AddHttpContextAccessor();
                    services.AddSupabaseAuthentication(configuration);
                    services.AddScoped<CustomerJwtSessionValidator>(sp =>
                        new CountingCustomerJwtSessionValidator(
                            sp.GetRequiredService<AppDbContext>(),
                            sp.GetRequiredService<ILogger<CustomerJwtSessionValidator>>(),
                            sp.GetRequiredService<SessionQueryCounter>()));
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
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .Start();

        return Task.FromResult(host);
    }

    private static async Task<HttpStatusCode> GetSessionStatusAsync(IHost host, string token)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync("/api/test/customer-session");
        return response.StatusCode;
    }
}

internal sealed class RecordingPasswordResetEmailQueue : IPasswordResetEmailQueue
{
    public List<PasswordResetEmailWork> Work { get; } = [];

    public void Enqueue(PasswordResetEmailWork work) => Work.Add(work);
}

public sealed class SessionQueryCounter
{
    public int Count { get; set; }
}

internal sealed class CountingCustomerJwtSessionValidator(
    AppDbContext dbContext,
    ILogger<CustomerJwtSessionValidator> logger,
    SessionQueryCounter counter) : CustomerJwtSessionValidator(dbContext, logger)
{
    public override async Task ValidateAsync(TokenValidatedContext context)
    {
        counter.Count++;
        await base.ValidateAsync(context);
    }
}

internal sealed class SessionControllerFeatureProvider(params Type[] controllerTypes)
    : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo) =>
        controllerTypes.Contains(typeInfo.AsType());
}

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/test/customer-session")]
public sealed class CustomerSessionProbeController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { ok = true });
}
