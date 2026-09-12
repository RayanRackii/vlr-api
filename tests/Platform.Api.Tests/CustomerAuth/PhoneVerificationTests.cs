using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class PhoneVerificationTests
{
    private const string EmailNotVerifiedMessage =
        "Email is not verified. Complete email verification first.";

    [Fact]
    public async Task RequestOtp_phone_contact_starts_on_that_phone_and_does_not_overwrite_name()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var phone = "+5511977773333";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: "named@club.test", phone: phone),
            CancellationToken.None);
        harness.Phone.StartedPhones.Clear();

        await harness.Auth.RequestOtpAsync(
            new RequestOtpDto { Name = "Nome Alterado", Contact = phone },
            CancellationToken.None);

        Assert.Single(harness.Phone.StartedPhones);
        Assert.Equal(phone, harness.Phone.StartedPhones[0]);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Phone == phone);
        Assert.Equal("Cliente Teste", customer.Name);
    }

    [Fact]
    public async Task RequestOtp_email_contact_starts_on_customer_phone()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "otpmail@club.test";
        var phone = "+5511966664444";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email, phone: phone),
            CancellationToken.None);
        harness.Phone.StartedPhones.Clear();

        await harness.Auth.RequestOtpAsync(
            new RequestOtpDto { Name = "Outro Nome", Contact = email },
            CancellationToken.None);

        Assert.Single(harness.Phone.StartedPhones);
        Assert.Equal(phone, harness.Phone.StartedPhones[0]);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.Equal("Cliente Teste", customer.Name);
    }

    [Fact]
    public async Task VerifyOtp_phone_only_pending_marks_phone_and_does_not_issue_jwt()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var phone = "+5511955555555";
        await harness.Auth.RequestOtpAsync(
            new RequestOtpDto { Name = "OTP User", Contact = phone },
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Auth.VerifyOtpAsync(
                new VerifyOtpDto { Contact = phone, Code = "123456" },
                CancellationToken.None));

        Assert.Equal(EmailNotVerifiedMessage, ex.Message);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Phone == phone);
        Assert.NotNull(customer.PhoneVerifiedAt);
        Assert.Null(customer.EmailVerifiedAt);
        Assert.Null(customer.Email);
        Assert.Empty(harness.Db.OtpCodes);
        Assert.Single(harness.Phone.StartedPhones);
    }

    [Fact]
    public async Task VerifyOtp_register_pending_marks_phone_login_still_unauthorized()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "pending-otp@club.test";
        var phone = "+5511944442222";
        var password = "secret123";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email, phone: phone, password: password),
            CancellationToken.None);
        harness.Phone.StartedPhones.Clear();

        await harness.Auth.RequestOtpAsync(
            new RequestOtpDto { Name = "Cliente Teste", Contact = phone },
            CancellationToken.None);

        var verifyEx = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Auth.VerifyOtpAsync(
                new VerifyOtpDto { Contact = phone, Code = "123456" },
                CancellationToken.None));

        Assert.Equal(EmailNotVerifiedMessage, verifyEx.Message);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.NotNull(customer.PhoneVerifiedAt);
        Assert.Null(customer.EmailVerifiedAt);

        var loginEx = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => harness.Auth.LoginAsync(
                new CustomerLoginRequestDto { Email = email, Password = password },
                CancellationToken.None));
        Assert.Equal(EmailNotVerifiedMessage, loginEx.Message);
    }

    [Fact]
    public async Task VerifyOtp_after_email_verified_returns_jwt_and_marks_phone()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var email = "verified-otp@club.test";
        var phone = "+5511933330000";
        var password = "secret123";
        await harness.Auth.RegisterAsync(
            CustomerAuthHarness.NewRegister(email: email, phone: phone, password: password),
            CancellationToken.None);
        var code = FakeEmailProvider.ExtractSixDigitCode(harness.Email.Sent[0].Body);
        await harness.Auth.VerifyEmailAsync(
            new VerifyEmailRequestDto { Email = email, Code = code },
            CancellationToken.None);

        var login = await harness.Auth.LoginAsync(
            new CustomerLoginRequestDto { Email = email, Password = password },
            CancellationToken.None);
        Assert.Equal("test-token", login.Token);
        Assert.True(login.Customer.EmailVerified);
        Assert.False(login.Customer.PhoneVerified);

        await harness.Auth.RequestOtpAsync(
            new RequestOtpDto { Name = "Cliente Teste", Contact = phone },
            CancellationToken.None);
        var auth = await harness.Auth.VerifyOtpAsync(
            new VerifyOtpDto { Contact = phone, Code = "123456" },
            CancellationToken.None);

        Assert.Equal("test-token", auth.Token);
        Assert.True(auth.Customer.EmailVerified);
        Assert.True(auth.Customer.PhoneVerified);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.NotNull(customer.EmailVerifiedAt);
        Assert.NotNull(customer.PhoneVerifiedAt);
    }

    [Fact]
    public async Task VerifyOtp_grandfathered_email_verified_returns_jwt()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var at = DateTimeOffset.Parse("2026-08-01T12:00:00Z");
        var phone = "+5511911110002";
        var customer = new Customer
        {
            TenantId = harness.Tenant.Id,
            Name = "Sócio Grandfathered",
            Email = "legacy-otp@club.test",
            Phone = phone,
            EmailVerifiedAt = at,
        };
        var hasher = new PasswordHasher<Customer>();
        customer.PasswordHash = hasher.HashPassword(customer, "secret123");
        harness.Db.Customers.Add(customer);
        await harness.Db.SaveChangesAsync();

        var login = await harness.Auth.LoginAsync(
            new CustomerLoginRequestDto { Email = "legacy-otp@club.test", Password = "secret123" },
            CancellationToken.None);
        Assert.Equal("test-token", login.Token);
        Assert.True(login.Customer.EmailVerified);
        Assert.False(login.Customer.PhoneVerified);

        await harness.Auth.RequestOtpAsync(
            new RequestOtpDto { Name = "Sócio Grandfathered", Contact = phone },
            CancellationToken.None);
        var auth = await harness.Auth.VerifyOtpAsync(
            new VerifyOtpDto { Contact = phone, Code = "123456" },
            CancellationToken.None);

        Assert.Equal("test-token", auth.Token);
        Assert.True(auth.Customer.EmailVerified);
        Assert.True(auth.Customer.PhoneVerified);
        var stored = await harness.Db.Customers.SingleAsync(c => c.Phone == phone);
        Assert.NotNull(stored.EmailVerifiedAt);
        Assert.NotNull(stored.PhoneVerifiedAt);
    }

    [Fact]
    public async Task Controller_maps_provider_to_503_and_rate_limit_to_429()
    {
        var providerController = CustomerAuthHarness.CreateController(
            new StubCustomerAuthService
            {
                ToThrow = new PhoneVerificationProviderException(
                    TwilioVerifyPhoneVerificationClient.ProviderUnavailableMessage),
            });
        var providerResult = await providerController.Register(
            CustomerAuthHarness.NewRegister(),
            "authclub",
            CancellationToken.None);
        var providerObject = Assert.IsType<ObjectResult>(providerResult.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, providerObject.StatusCode);
        Assert.Contains("unavailable", ErrorText(providerObject));

        var rateController = CustomerAuthHarness.CreateController(
            new StubCustomerAuthService
            {
                ToThrow = new PhoneVerificationRateLimitedException(
                    TwilioVerifyPhoneVerificationClient.RateLimitedMessage),
            });
        var rateResult = await rateController.Register(
            CustomerAuthHarness.NewRegister(),
            "authclub",
            CancellationToken.None);
        var rateObject = Assert.IsType<ObjectResult>(rateResult.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rateObject.StatusCode);
        Assert.Contains("Too many verification attempts", ErrorText(rateObject));

        var verifyProviderController = CustomerAuthHarness.CreateController(
            new StubCustomerAuthService
            {
                ToThrow = new PhoneVerificationProviderException(
                    TwilioVerifyPhoneVerificationClient.ProviderUnavailableMessage),
            });
        var verifyProviderResult = await verifyProviderController.VerifyEmail(
            new VerifyEmailRequestDto { Email = "a@club.test", Code = "123456" },
            "authclub",
            CancellationToken.None);
        var verifyProviderObject = Assert.IsType<ObjectResult>(verifyProviderResult.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, verifyProviderObject.StatusCode);
    }

    [Fact]
    public async Task Controller_maps_invalid_to_401_on_verify_and_400_on_resend()
    {
        var invalid = new PhoneVerificationInvalidException("Invalid or expired verification code.");
        var verifyController = CustomerAuthHarness.CreateController(
            new StubCustomerAuthService { ToThrow = invalid });
        var verifyResult = await verifyController.VerifyEmail(
            new VerifyEmailRequestDto { Email = "a@club.test", Code = "123456" },
            "authclub",
            CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(verifyResult.Result);

        var resendController = CustomerAuthHarness.CreateController(
            new StubCustomerAuthService { ToThrow = invalid });
        var resendResult = await resendController.ResendVerification(
            new ResendVerificationRequestDto { Email = "a@club.test" },
            "authclub",
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(resendResult);
    }

    private static string ErrorText(ObjectResult result) =>
        JsonSerializer.Serialize(result.Value);
}
