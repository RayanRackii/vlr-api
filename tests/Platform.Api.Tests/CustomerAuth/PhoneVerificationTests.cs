using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class PhoneVerificationTests
{
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
    public async Task VerifyOtp_approved_marks_phone_verified_and_returns_jwt()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
        var phone = "+5511955555555";
        await harness.Auth.RequestOtpAsync(
            new RequestOtpDto { Name = "OTP User", Contact = phone },
            CancellationToken.None);

        var auth = await harness.Auth.VerifyOtpAsync(
            new VerifyOtpDto { Contact = phone, Code = "123456" },
            CancellationToken.None);

        Assert.Equal("test-token", auth.Token);
        Assert.True(auth.Customer.PhoneVerified);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Phone == phone);
        Assert.NotNull(customer.PhoneVerifiedAt);
        Assert.Null(customer.EmailVerifiedAt);
        Assert.Empty(harness.Db.OtpCodes);
        Assert.Single(harness.Phone.StartedPhones);
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
