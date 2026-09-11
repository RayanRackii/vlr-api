using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Modules.CustomerAuth.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class EmailVerificationTests
{
    [Fact]
    public async Task Register_sends_email_not_twilio()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "new@club.test";

        var response = await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);

        Assert.True(response.RequiresEmailVerification);
        Assert.True(response.RequiresPhoneVerification);
        Assert.True(response.VerificationStarted);
        Assert.Empty(harness.Phone.StartedPhones);
        Assert.Equal(1, harness.Email.SendCount);
        Assert.Equal(email, harness.Email.Sent[0].Recipient);
        Assert.Equal(RolvixEmailLayout.EmailVerificationSubject, harness.Email.Sent[0].Subject);
        var customer = await harness.Db.Customers.FindAsync(response.CustomerId);
        Assert.Null(customer!.EmailVerifiedAt);
        Assert.Null(customer.PhoneVerifiedAt);
        var otp = await harness.Db.OtpCodes.SingleAsync();
        Assert.Null(otp.Code);
        Assert.False(string.IsNullOrWhiteSpace(otp.CodeHash));
        Assert.Equal(OtpPurposes.EmailVerification, otp.Purpose);
    }

    [Fact]
    public async Task Register_response_serializes_phone_alias()
    {
        var dto = new RegisterCustomerResponseDto(Guid.NewGuid(), true, true);
        var json = JsonSerializer.Serialize(
            dto,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("\"requiresEmailVerification\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"requiresPhoneVerification\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correct_otp_sets_email_verified_issues_jwt_and_allows_login()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "verify@club.test";
        var password = "secret123";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email, password: password),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);

        var auth = await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = code },
            CancellationToken.None);

        Assert.Equal("test-token", auth.Token);
        Assert.True(auth.Customer.EmailVerified);
        Assert.False(auth.Customer.PhoneVerified);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.NotNull(customer.EmailVerifiedAt);
        Assert.Null(customer.PhoneVerifiedAt);

        var login = await harness.Auth.LoginAsync(
            new CustomerLoginRequestDto { Email = email, Password = password },
            CancellationToken.None);
        Assert.Equal("test-token", login.Token);
        Assert.True(login.Customer.EmailVerified);
    }

    [Fact]
    public async Task Wrong_otp_is_rejected_and_leaves_email_unverified()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "badcode@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);

        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);
        var wrong = code == "000000" ? "000001" : "000000";

        var ex = await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.VerifyEmailAsync(
                new VerifyEmailRequestDto { Email = email, Code = wrong },
                CancellationToken.None));

        Assert.Equal("Invalid or expired verification code.", ex.Message);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.Null(customer.EmailVerifiedAt);
    }

    [Fact]
    public async Task Expired_otp_is_rejected()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-09-11T12:00:00Z"));
        await using var harness = await CustomerAuthHarness.CreateAsync(timeProvider: time);
        var email = "expired@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);

        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.VerifyEmailAsync(
                new VerifyEmailRequestDto { Email = email, Code = code },
                CancellationToken.None));
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.Null(customer.EmailVerifiedAt);
    }

    [Fact]
    public async Task Otp_cannot_be_reused()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "reuse@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);

        await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = code },
            CancellationToken.None);

        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.VerifyEmailAsync(
                new VerifyEmailRequestDto { Email = email, Code = code },
                CancellationToken.None));
    }

    [Fact]
    public async Task Resend_invalidates_old_otp_and_new_otp_succeeds()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "resend@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        var oldCode = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);

        await harness.Auth.ResendVerificationAsync(
            new ResendVerificationRequestDto { Email = email },
            CancellationToken.None);

        Assert.Equal(2, harness.Email.SendCount);
        var newCode = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[1].Body);
        Assert.Empty(harness.Phone.StartedPhones);

        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.VerifyEmailAsync(
                new VerifyEmailRequestDto { Email = email, Code = oldCode },
                CancellationToken.None));

        var auth = await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = newCode },
            CancellationToken.None);
        Assert.True(auth.Customer.EmailVerified);
    }

    [Fact]
    public async Task Resend_within_cooldown_does_not_send_second_email()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-09-11T12:00:00Z"));
        var gate = new PhoneVerificationSendGate(cache, time);
        await using var harness = await CustomerAuthHarness.CreateAsync(
            sendGate: gate,
            timeProvider: time);
        var email = "cooldown@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        Assert.Equal(1, harness.Email.SendCount);

        await harness.Auth.ResendVerificationAsync(
            new ResendVerificationRequestDto { Email = email },
            CancellationToken.None);
        Assert.Equal(1, harness.Email.SendCount);

        time.Advance(TimeSpan.FromSeconds(46));
        await harness.Auth.ResendVerificationAsync(
            new ResendVerificationRequestDto { Email = email },
            CancellationToken.None);
        Assert.Equal(2, harness.Email.SendCount);
    }

    [Fact]
    public async Task Five_failed_attempts_then_correct_code_is_still_rejected()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "lockout@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < CustomerVerificationCodeService.MaxAttempts; i++)
        {
            await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
                () => harness.Auth.VerifyEmailAsync(
                    new VerifyEmailRequestDto { Email = email, Code = wrong },
                    CancellationToken.None));
        }

        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.VerifyEmailAsync(
                new VerifyEmailRequestDto { Email = email, Code = code },
                CancellationToken.None));
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.Null(customer.EmailVerifiedAt);
    }

    [Fact]
    public async Task Otp_from_tenant_a_cannot_verify_same_email_in_tenant_b()
    {
        var databaseName = $"email-iso-{Guid.NewGuid():N}";
        var tenantA = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantB = new Tenant("Club B", "11222333000181", subdomain: "club-b");
        var tenantProvider = new FakeTenantProvider { TenantId = tenantA.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider, databaseName);
        db.Tenants.Add(tenantA);
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        var phone = new FakePhoneVerificationClient();
        var emailProvider = new FakeEmailProvider();
        var auth = CustomerAuthHarness.CreateService(
            db,
            tenantProvider,
            phone,
            emailProvider,
            new AllowAllPhoneVerificationSendGate(),
            TimeProvider.System);

        var request = CustomerAuthHarness.NewRegister(email: "shared@club.test", phone: "+5511933331111");
        await auth.RegisterAsync(request, CancellationToken.None);
        var codeA = FakeEmailProvider.ExtractSixDigitCode(emailProvider.Sent[0].Body);

        tenantProvider.TenantId = tenantB.Id;
        await auth.RegisterAsync(request, CancellationToken.None);
        var codeB = FakeEmailProvider.ExtractSixDigitCode(emailProvider.Sent[1].Body);

        await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => auth.VerifyEmailAsync(
                new VerifyEmailRequestDto { Email = "shared@club.test", Code = codeA },
                CancellationToken.None));

        var verified = await auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = "shared@club.test", Code = codeB },
            CancellationToken.None);
        Assert.True(verified.Customer.EmailVerified);
        Assert.Equal(tenantB.Id, verified.Customer.TenantId);
        Assert.Empty(phone.StartedPhones);

        tenantProvider.TenantId = tenantA.Id;
        Assert.Equal(1, await db.Customers.CountAsync());
        Assert.Null((await db.Customers.SingleAsync()).EmailVerifiedAt);
    }

    [Fact]
    public async Task Register_pending_in_tenant_a_does_not_block_tenant_b()
    {
        var databaseName = $"pending-iso-{Guid.NewGuid():N}";
        var tenantA = new Tenant("Club A", "66666666000191", subdomain: "club-a");
        var tenantB = new Tenant("Club B", "11222333000181", subdomain: "club-b");
        var tenantProvider = new FakeTenantProvider { TenantId = tenantA.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider, databaseName);
        db.Tenants.Add(tenantA);
        db.Tenants.Add(tenantB);
        await db.SaveChangesAsync();

        var phone = new FakePhoneVerificationClient();
        var emailProvider = new FakeEmailProvider();
        var auth = CustomerAuthHarness.CreateService(
            db,
            tenantProvider,
            phone,
            emailProvider,
            new AllowAllPhoneVerificationSendGate(),
            TimeProvider.System);

        var request = CustomerAuthHarness.NewRegister(email: "shared@club.test", phone: "+5511933331111");
        var fromA = await auth.RegisterAsync(request, CancellationToken.None);

        tenantProvider.TenantId = tenantB.Id;
        var fromB = await auth.RegisterAsync(request, CancellationToken.None);

        Assert.NotEqual(fromA.CustomerId, fromB.CustomerId);
        Assert.Empty(phone.StartedPhones);

        tenantProvider.TenantId = tenantA.Id;
        Assert.Equal(1, await db.Customers.CountAsync());
        Assert.Equal(fromA.CustomerId, (await db.Customers.SingleAsync()).Id);

        tenantProvider.TenantId = tenantB.Id;
        Assert.Equal(1, await db.Customers.CountAsync());
        Assert.Equal(fromB.CustomerId, (await db.Customers.SingleAsync()).Id);
    }

    [Fact]
    public async Task Email_is_normalized_on_register_and_verify()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: "User@Club.test"),
            CancellationToken.None);

        Assert.Equal("user@club.test", harness.Email.Sent[0].Recipient);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);

        var auth = await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = "user@club.test", Code = code },
            CancellationToken.None);
        Assert.True(auth.Customer.EmailVerified);
        Assert.Equal("user@club.test", auth.Customer.Email);
    }

    [Fact]
    public async Task Phone_verification_is_not_required_for_login()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "nophonegate@club.test";
        var password = "secret123";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email, password: password),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);
        await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = code },
            CancellationToken.None);

        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.NotNull(customer.EmailVerifiedAt);
        Assert.Null(customer.PhoneVerifiedAt);

        var login = await harness.Auth.LoginAsync(
            new CustomerLoginRequestDto { Email = email, Password = password },
            CancellationToken.None);
        Assert.True(login.Customer.EmailVerified);
        Assert.False(login.Customer.PhoneVerified);
    }

    [Fact]
    public async Task Twilio_is_not_invoked_by_register_resend_or_verify_email()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "notwilio@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        await harness.Auth.ResendVerificationAsync(
            new ResendVerificationRequestDto { Email = email },
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[^1].Body);
        await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = code },
            CancellationToken.None);

        Assert.Empty(harness.Phone.StartedPhones);
    }

    [Fact]
    public async Task Provider_failure_on_register_keeps_customer_and_reports_not_started()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        harness.Email.ThrowHttpRequestException = true;
        var email = "kept@club.test";

        var response = await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);

        Assert.False(response.VerificationStarted);
        Assert.True(response.RequiresEmailVerification);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.Equal(response.CustomerId, customer.Id);
        Assert.Null(customer.EmailVerifiedAt);
        Assert.Empty(harness.Phone.StartedPhones);
        Assert.Equal(0, harness.Email.SendCount);
    }

    [Fact]
    public async Task Provider_failure_on_resend_does_not_throw()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "resend-fail@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        harness.Email.ThrowHttpRequestException = true;

        await harness.Auth.ResendVerificationAsync(
            new ResendVerificationRequestDto { Email = email },
            CancellationToken.None);
    }

    [Fact]
    public async Task Login_unverified_throws_unauthorized()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "unverified-login@club.test";
        var password = "secret123";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email, password: password),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Auth.LoginAsync(
                new CustomerLoginRequestDto { Email = email, Password = password },
                CancellationToken.None));

        Assert.Equal("Email is not verified. Complete email verification first.", ex.Message);
    }

    [Fact]
    public async Task Register_retries_same_pending_triple_resume_without_twilio()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "resume@club.test";
        var phone = "+5511999991111";
        var first = await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(
                email: email,
                phone: phone,
                name: "Primeiro Nome",
                password: "secret123"),
            CancellationToken.None);

        var second = await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(
                email: email,
                phone: phone,
                name: "Nome Atualizado",
                password: "newpass12"),
            CancellationToken.None);

        Assert.Equal(first.CustomerId, second.CustomerId);
        Assert.Empty(harness.Phone.StartedPhones);
        Assert.Equal(2, harness.Email.SendCount);
        Assert.Equal(1, await harness.Db.Customers.CountAsync());
        var customer = await harness.Db.Customers.SingleAsync(c => c.Id == first.CustomerId);
        Assert.Equal("Nome Atualizado", customer.Name);
        Assert.Null(customer.EmailVerifiedAt);
        var hasher = new PasswordHasher<Customer>();
        Assert.Equal(
            PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(customer, customer.PasswordHash!, "newpass12"));
    }

    [Fact]
    public async Task Register_verified_duplicate_email_is_rejected()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "taken@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email, phone: "+5511966664444", document: "529.982.247-25"),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);
        await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = code },
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Auth.RegisterAsync(
                CustomerAuthHarness.NewRegister(email: email, phone: "+5511955550000", document: "39053344705"),
                CancellationToken.None));

        Assert.Equal("A customer with this email already exists.", ex.Message);
        Assert.Equal(1, await harness.Db.Customers.CountAsync());
    }

    [Fact]
    public async Task Register_pending_partial_overlap_is_rejected()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(
                email: "partial@club.test",
                phone: "+5511944440001",
                document: "529.982.247-25"),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Auth.RegisterAsync(
                CustomerAuthHarness.NewRegister(
                    email: "partial@club.test",
                    phone: "+5511944440002",
                    document: "39053344705"),
                CancellationToken.None));

        Assert.Equal(1, await harness.Db.Customers.CountAsync());
    }

    [Fact]
    public async Task Register_resume_within_cooldown_skips_second_send()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-09-11T12:00:00Z"));
        var gate = new PhoneVerificationSendGate(cache, time);
        await using var harness = await CustomerAuthHarness.CreateAsync(
            sendGate: gate,
            timeProvider: time);
        var request = CustomerAuthHarness.NewRegister(
            email: "cooldown-reg@club.test",
            phone: "+5511922220001");

        var first = await harness.Auth.RegisterAsync(request, CancellationToken.None);
        var second = await harness.Auth.RegisterAsync(request, CancellationToken.None);

        Assert.True(first.VerificationStarted);
        Assert.True(second.VerificationStarted);
        Assert.Equal(first.CustomerId, second.CustomerId);
        Assert.Equal(1, harness.Email.SendCount);
        Assert.Empty(harness.Phone.StartedPhones);
    }

    [Fact]
    public async Task Register_ip_limited_skips_send_without_throwing()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new PhoneVerificationSendGate(cache, TimeProvider.System);
        FillIpLimit(gate);

        await using var harness = await CustomerAuthHarness.CreateAsync(sendGate: gate);
        var response = await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: "limit-over@club.test"),
            CancellationToken.None);

        Assert.False(response.VerificationStarted);
        Assert.Equal(0, harness.Email.SendCount);
        Assert.Empty(harness.Phone.StartedPhones);
        Assert.Equal(1, await harness.Db.Customers.CountAsync());
    }

    [Fact]
    public async Task Resend_ip_limited_throws_rate_limit()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var gate = new PhoneVerificationSendGate(cache, TimeProvider.System);
        FillIpLimit(gate);

        await using var harness = await CustomerAuthHarness.CreateAsync(sendGate: gate);
        var email = "after-limit@club.test";
        var registered = await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        Assert.False(registered.VerificationStarted);

        await Assert.ThrowsAsync<PhoneVerificationRateLimitedException>(
            () => harness.Auth.ResendVerificationAsync(
                new ResendVerificationRequestDto { Email = email },
                CancellationToken.None));
        Assert.Equal(0, harness.Email.SendCount);
    }

    [Fact]
    public async Task Resend_unknown_email_when_ip_limited_throws_rate_limit()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));
        var gate = new PhoneVerificationSendGate(cache, time);
        await using var harness = await CustomerAuthHarness.CreateAsync(sendGate: gate);

        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            await harness.Auth.ResendVerificationAsync(
                new ResendVerificationRequestDto { Email = $"unknown{i}@club.test" },
                CancellationToken.None);
        }

        await Assert.ThrowsAsync<PhoneVerificationRateLimitedException>(
            () => harness.Auth.ResendVerificationAsync(
                new ResendVerificationRequestDto { Email = "still-unknown@club.test" },
                CancellationToken.None));
        Assert.Equal(0, harness.Email.SendCount);
        Assert.Empty(harness.Phone.StartedPhones);
    }

    [Fact]
    public async Task Resend_already_email_verified_does_not_send()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "verified-resend@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);
        await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = code },
            CancellationToken.None);
        harness.Email.Sent.Clear();

        await harness.Auth.ResendVerificationAsync(
            new ResendVerificationRequestDto { Email = email },
            CancellationToken.None);

        Assert.Equal(0, harness.Email.SendCount);
        Assert.Empty(harness.Phone.StartedPhones);
    }

    [Fact]
    public async Task Controller_verify_email_invalid_returns_401()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "ctrl-invalid@club.test";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email),
            CancellationToken.None);
        var controller = CustomerAuthHarness.CreateController(harness.Auth);

        var result = await controller.VerifyEmail(
            new VerifyEmailRequestDto { Email = email, Code = "999999" },
            "authclub",
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Controller_resend_unknown_returns_202()
    {
        var controller = CustomerAuthHarness.CreateController(new StubCustomerAuthService());
        var result = await controller.ResendVerification(
            new ResendVerificationRequestDto { Email = "missing@club.test" },
            "authclub",
            CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
    }

    [Fact]
    public async Task Controller_register_does_not_return_503_when_email_provider_fails()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        harness.Email.ThrowHttpRequestException = true;
        var controller = CustomerAuthHarness.CreateController(harness.Auth);

        var result = await controller.Register(
            CustomerAuthHarness.NewRegister(email: "no-503@club.test"),
            "authclub",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<RegisterCustomerResponseDto>(ok.Value);
        Assert.False(dto.VerificationStarted);
        Assert.True(dto.RequiresEmailVerification);
    }

    [Fact]
    public async Task Grandfathered_phone_and_email_verified_customer_can_login()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var at = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var customer = new Customer
        {
            TenantId = harness.Tenant.Id,
            Name = "Sócio Antigo",
            Email = "legacy@club.test",
            Phone = "+5511911110000",
            PhoneVerifiedAt = at,
            EmailVerifiedAt = at,
        };
        var hasher = new PasswordHasher<Customer>();
        customer.PasswordHash = hasher.HashPassword(customer, "secret123");
        harness.Db.Customers.Add(customer);
        await harness.Db.SaveChangesAsync();

        var login = await harness.Auth.LoginAsync(
            new CustomerLoginRequestDto { Email = "legacy@club.test", Password = "secret123" },
            CancellationToken.None);

        Assert.Equal("test-token", login.Token);
        Assert.True(login.Customer.EmailVerified);
        Assert.True(login.Customer.PhoneVerified);
    }

    [Fact]
    public void Email_verification_body_has_no_paragraph_tags()
    {
        var inner = RolvixEmailLayout.EmailVerificationBody("123456");
        var html = RolvixEmailLayout.Wrap("Ana", inner);

        Assert.DoesNotContain("<p", inner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<p", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("123456", inner, StringComparison.Ordinal);
        Assert.Contains("Seu código de verificação da Rolvix é:", inner, StringComparison.Ordinal);
        Assert.Contains("Este código expira em 10 minutos.", inner, StringComparison.Ordinal);
        Assert.Contains("Se você não solicitou este código, ignore esta mensagem.", inner, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_up_contains_email_verified_at_backfill()
    {
        var migrationsDir = FindMigrationsDirectory();
        var files = Directory.GetFiles(migrationsDir, "*B2cEmailVerification.cs")
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Single(files);
        var sql = File.ReadAllText(files[0]);
        Assert.Contains(
            "UPDATE core.customers SET email_verified_at = phone_verified_at WHERE phone_verified_at IS NOT NULL;",
            sql,
            StringComparison.Ordinal);
    }

    private static void FillIpLimit(PhoneVerificationSendGate gate)
    {
        var tenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            Assert.Equal(
                PhoneVerificationSendDecision.Send,
                gate.Decide(tenantId, $"prefill{i}@club.test", clientIp: null));
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

        throw new InvalidOperationException("Could not locate EF migrations directory.");
    }
}
