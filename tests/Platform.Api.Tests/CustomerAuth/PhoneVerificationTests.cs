using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Controllers;
using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Modules.CustomerAuth.Services;
using Platform.Api.Modules.RegistrationFields.Dtos;
using Platform.Api.Modules.RegistrationFields.Services;
using Platform.Api.Services.Brazil;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class PhoneVerificationTests
{
    [Fact]
    public async Task Register_starts_verification_on_customer_phone()
    {
        await using var harness = await AuthHarness.CreateAsync();
        var phone = "+5511999991111";

        var response = await harness.Auth.RegisterAsync(
            NewRegister(email: "new@club.test", phone: phone),
            CancellationToken.None);

        Assert.True(response.RequiresPhoneVerification);
        Assert.Single(harness.Phone.StartedPhones);
        Assert.Equal(phone, harness.Phone.StartedPhones[0]);
        Assert.Empty(harness.Db.OtpCodes);
        var customer = await harness.Db.Customers.FindAsync(response.CustomerId);
        Assert.Null(customer!.PhoneVerifiedAt);
    }

    [Fact]
    public async Task Register_provider_error_keeps_customer_unverified()
    {
        await using var harness = await AuthHarness.CreateAsync();
        harness.Phone.StartThrowsProvider = true;
        var email = "kept@club.test";

        await Assert.ThrowsAsync<PhoneVerificationProviderException>(
            () => harness.Auth.RegisterAsync(
                NewRegister(email: email),
                CancellationToken.None));

        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.Null(customer.PhoneVerifiedAt);
        Assert.Equal("+5511999991111", customer.Phone);
        Assert.Empty(harness.Db.OtpCodes);
    }

    [Fact]
    public async Task Register_rate_limit_throws_rate_limit_exception()
    {
        await using var harness = await AuthHarness.CreateAsync();
        harness.Phone.StartThrowsRateLimit = true;

        await Assert.ThrowsAsync<PhoneVerificationRateLimitedException>(
            () => harness.Auth.RegisterAsync(NewRegister(), CancellationToken.None));
    }

    [Fact]
    public async Task VerifyPhone_approved_marks_verified_and_returns_jwt()
    {
        await using var harness = await AuthHarness.CreateAsync();
        var email = "verify@club.test";
        await harness.Auth.RegisterAsync(NewRegister(email: email), CancellationToken.None);

        var auth = await harness.Auth.VerifyPhoneAsync(
            new VerifyPhoneRequestDto { Email = email, Code = "123456" },
            CancellationToken.None);

        Assert.Equal("test-token", auth.Token);
        Assert.True(auth.Customer.PhoneVerified);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.NotNull(customer.PhoneVerifiedAt);
        Assert.Empty(harness.Db.OtpCodes);
    }

    [Fact]
    public async Task VerifyPhone_invalid_code_leaves_phone_unverified()
    {
        await using var harness = await AuthHarness.CreateAsync();
        var email = "badcode@club.test";
        await harness.Auth.RegisterAsync(NewRegister(email: email), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<PhoneVerificationInvalidException>(
            () => harness.Auth.VerifyPhoneAsync(
                new VerifyPhoneRequestDto { Email = email, Code = "000000" },
                CancellationToken.None));

        Assert.Equal("Invalid or expired verification code.", ex.Message);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Email == email);
        Assert.Null(customer.PhoneVerifiedAt);
    }

    [Fact]
    public async Task VerifyPhone_rate_limited_and_provider_error()
    {
        await using var harness = await AuthHarness.CreateAsync();
        var email = "checkfail@club.test";
        await harness.Auth.RegisterAsync(NewRegister(email: email), CancellationToken.None);

        harness.Phone.CheckThrowsRateLimit = true;
        await Assert.ThrowsAsync<PhoneVerificationRateLimitedException>(
            () => harness.Auth.VerifyPhoneAsync(
                new VerifyPhoneRequestDto { Email = email, Code = "123456" },
                CancellationToken.None));

        harness.Phone.CheckThrowsRateLimit = false;
        harness.Phone.CheckThrowsProvider = true;
        await Assert.ThrowsAsync<PhoneVerificationProviderException>(
            () => harness.Auth.VerifyPhoneAsync(
                new VerifyPhoneRequestDto { Email = email, Code = "123456" },
                CancellationToken.None));
    }

    [Fact]
    public async Task Resend_starts_on_existing_customer_phone()
    {
        await using var harness = await AuthHarness.CreateAsync();
        var email = "resend@club.test";
        var phone = "+5511988882222";
        await harness.Auth.RegisterAsync(NewRegister(email: email, phone: phone), CancellationToken.None);
        harness.Phone.StartedPhones.Clear();

        await harness.Auth.ResendVerificationAsync(
            new ResendVerificationRequestDto { Email = email },
            CancellationToken.None);

        Assert.Single(harness.Phone.StartedPhones);
        Assert.Equal(phone, harness.Phone.StartedPhones[0]);
    }

    [Fact]
    public async Task Resend_missing_customer_throws_not_found()
    {
        await using var harness = await AuthHarness.CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.Auth.ResendVerificationAsync(
                new ResendVerificationRequestDto { Email = "missing@club.test" },
                CancellationToken.None));
    }

    [Fact]
    public async Task RequestOtp_phone_contact_starts_on_that_phone_and_does_not_overwrite_name()
    {
        await using var harness = await AuthHarness.CreateAsync();
        var phone = "+5511977773333";
        await harness.Auth.RegisterAsync(
            NewRegister(email: "named@club.test", phone: phone),
            CancellationToken.None);
        harness.Phone.StartedPhones.Clear();

        await harness.Auth.RequestOtpAsync(
            new RequestOtpDto { Name = "Nome Alterado", Contact = phone },
            CancellationToken.None);

        Assert.Single(harness.Phone.StartedPhones);
        Assert.Equal(phone, harness.Phone.StartedPhones[0]);
        var customer = await harness.Db.Customers.SingleAsync(c => c.Phone == phone);
        Assert.Equal("Cliente Teste", customer.Name);
        Assert.Empty(harness.Db.OtpCodes);
    }

    [Fact]
    public async Task RequestOtp_email_contact_starts_on_customer_phone()
    {
        await using var harness = await AuthHarness.CreateAsync();
        var email = "otpmail@club.test";
        var phone = "+5511966664444";
        await harness.Auth.RegisterAsync(NewRegister(email: email, phone: phone), CancellationToken.None);
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
    public async Task VerifyOtp_approved_marks_verified_and_returns_jwt()
    {
        await using var harness = await AuthHarness.CreateAsync();
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
        Assert.Empty(harness.Db.OtpCodes);
    }

    [Fact]
    public async Task Controller_maps_provider_to_503_and_rate_limit_to_429()
    {
        var providerController = CreateController(
            new StubAuthService
            {
                ToThrow = new PhoneVerificationProviderException(
                    TwilioVerifyPhoneVerificationClient.ProviderUnavailableMessage),
            });
        var providerResult = await providerController.Register(
            NewRegister(),
            "authclub",
            CancellationToken.None);
        var providerObject = Assert.IsType<ObjectResult>(providerResult.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, providerObject.StatusCode);
        Assert.Contains("unavailable", ErrorText(providerObject));

        var rateController = CreateController(
            new StubAuthService
            {
                ToThrow = new PhoneVerificationRateLimitedException(
                    TwilioVerifyPhoneVerificationClient.RateLimitedMessage),
            });
        var rateResult = await rateController.Register(
            NewRegister(),
            "authclub",
            CancellationToken.None);
        var rateObject = Assert.IsType<ObjectResult>(rateResult.Result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rateObject.StatusCode);
        Assert.Contains("Too many verification attempts", ErrorText(rateObject));
    }

    [Fact]
    public async Task Controller_maps_invalid_to_401_on_verify_and_400_on_resend()
    {
        var invalid = new PhoneVerificationInvalidException("Invalid or expired verification code.");
        var verifyController = CreateController(new StubAuthService { ToThrow = invalid });
        var verifyResult = await verifyController.VerifyPhone(
            new VerifyPhoneRequestDto { Email = "a@club.test", Code = "123456" },
            "authclub",
            CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(verifyResult.Result);

        var resendController = CreateController(new StubAuthService { ToThrow = invalid });
        var resendResult = await resendController.ResendVerification(
            new ResendVerificationRequestDto { Email = "a@club.test" },
            "authclub",
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(resendResult);
    }

    private static string ErrorText(ObjectResult result) =>
        System.Text.Json.JsonSerializer.Serialize(result.Value);

    private static CustomerAuthController CreateController(ICustomerAuthService auth) =>
        new(auth, new UnusedRegistrationFields(), new NoOpTenantBinder());

    private static RegisterCustomerRequestDto NewRegister(
        string? email = null,
        string? phone = null) =>
        new()
        {
            Name = "Cliente Teste",
            Email = email ?? $"{Guid.NewGuid():N}@club.test",
            Password = "secret123",
            Phone = phone ?? "+5511999991111",
            CustomerType = CustomerType.Individual,
            Document = "529.982.247-25",
        };

    private sealed class AuthHarness(
        AppDbContext db,
        CustomerAuthService auth,
        FakePhoneVerificationClient phone) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public CustomerAuthService Auth { get; } = auth;
        public FakePhoneVerificationClient Phone { get; } = phone;

        public static async Task<AuthHarness> CreateAsync()
        {
            var tenant = new Tenant("Auth Club", "66666666000191", subdomain: "authclub");
            var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
            var db = InMemoryAppDb.Create(tenantProvider);
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            var phone = new FakePhoneVerificationClient();
            var auth = new CustomerAuthService(
                db,
                tenantProvider,
                new FakeJwtIssuer(),
                new FakeViaCep(),
                new UnusedRegistrationFields(),
                phone,
                NullLogger<CustomerAuthService>.Instance);
            return new AuthHarness(db, auth, phone);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class FakeJwtIssuer : ICustomerJwtIssuer
    {
        public string IssueToken(Customer customer) => "test-token";
    }

    private sealed class FakeViaCep : IViaCepClient
    {
        public Task<ViaCepAddress> LookupAsync(string postalCode, CancellationToken cancellationToken) =>
            Task.FromResult(new ViaCepAddress(postalCode, "Rua", "Bairro", "Cidade", "SP"));
    }

    private sealed class UnusedRegistrationFields : IRegistrationFieldService
    {
        public Task<RegistrationSchemaResponseDto> GetSchemaBySubdomainAsync(
            string subdomain,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RegistrationSchemaResponseDto([], []));

        public Task<IReadOnlyList<RegistrationFieldDto>> ListForTenantAsync(
            Guid tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RegistrationFieldDto>>([]);

        public Task<RegistrationFieldDto> CreateAsync(
            Guid tenantId,
            UpsertRegistrationFieldRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<RegistrationFieldDto> UpdateAsync(
            Guid tenantId,
            Guid fieldId,
            UpdateRegistrationFieldRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task DeleteAsync(Guid tenantId, Guid fieldId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class NoOpTenantBinder : IPublicTenantBinder
    {
        public Task BindFromSubdomainAsync(string? subdomain, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubAuthService : ICustomerAuthService
    {
        public Exception? ToThrow { get; init; }

        public Task RequestOtpAsync(RequestOtpDto request, CancellationToken cancellationToken) =>
            Fail();

        public Task<AuthResponseDto> VerifyOtpAsync(
            VerifyOtpDto request,
            CancellationToken cancellationToken) =>
            Fail<AuthResponseDto>();

        public Task<RegisterCustomerResponseDto> RegisterAsync(
            RegisterCustomerRequestDto request,
            CancellationToken cancellationToken) =>
            Fail<RegisterCustomerResponseDto>();

        public Task<AuthResponseDto> VerifyPhoneAsync(
            VerifyPhoneRequestDto request,
            CancellationToken cancellationToken) =>
            Fail<AuthResponseDto>();

        public Task ResendVerificationAsync(
            ResendVerificationRequestDto request,
            CancellationToken cancellationToken) =>
            Fail();

        public Task<AuthResponseDto> LoginAsync(
            CustomerLoginRequestDto request,
            CancellationToken cancellationToken) =>
            Fail<AuthResponseDto>();

        public Task<TenantBrandingResponseDto> GetBrandingAsync(
            string subdomain,
            CancellationToken cancellationToken) =>
            Fail<TenantBrandingResponseDto>();

        public Task<CustomerProfileDto> GetCurrentAsync(
            Guid customerId,
            CancellationToken cancellationToken) =>
            Fail<CustomerProfileDto>();

        public Task<CustomerProfileDto> UpdateProfileAsync(
            Guid customerId,
            UpdateCustomerProfileRequestDto request,
            CancellationToken cancellationToken) =>
            Fail<CustomerProfileDto>();

        private Task Fail()
        {
            throw ToThrow ?? new NotImplementedException();
        }

        private Task<T> Fail<T>()
        {
            throw ToThrow ?? new NotImplementedException();
        }
    }
}
