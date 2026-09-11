using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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

internal sealed class CustomerAuthHarness : IAsyncDisposable
{
    public const string JwtSecret = "customer-verification-test-secret-key-32b!";

    private CustomerAuthHarness(
        AppDbContext db,
        Tenant tenant,
        FakeTenantProvider tenantProvider,
        CustomerAuthService auth,
        FakePhoneVerificationClient phone,
        FakeEmailProvider email,
        TimeProvider time)
    {
        Db = db;
        Tenant = tenant;
        TenantProvider = tenantProvider;
        Auth = auth;
        Phone = phone;
        Email = email;
        Time = time;
    }

    public AppDbContext Db { get; }

    public Tenant Tenant { get; }

    public FakeTenantProvider TenantProvider { get; }

    public CustomerAuthService Auth { get; }

    public FakePhoneVerificationClient Phone { get; }

    public FakeEmailProvider Email { get; }

    public TimeProvider Time { get; }

    public static async Task<CustomerAuthHarness> CreateAsync(
        IPhoneVerificationSendGate? sendGate = null,
        TimeProvider? timeProvider = null,
        IHttpContextAccessor? httpContextAccessor = null,
        string? databaseName = null)
    {
        var tenant = new Tenant("Auth Club", "66666666000191", subdomain: "authclub");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        var db = InMemoryAppDb.Create(tenantProvider, databaseName);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var time = timeProvider ?? TimeProvider.System;
        var phone = new FakePhoneVerificationClient();
        var email = new FakeEmailProvider();
        var auth = CreateService(
            db,
            tenantProvider,
            phone,
            email,
            sendGate ?? new AllowAllPhoneVerificationSendGate(),
            time,
            httpContextAccessor);

        return new CustomerAuthHarness(db, tenant, tenantProvider, auth, phone, email, time);
    }

    public static CustomerAuthService CreateService(
        AppDbContext db,
        ITenantProvider tenantProvider,
        FakePhoneVerificationClient phone,
        FakeEmailProvider email,
        IPhoneVerificationSendGate sendGate,
        TimeProvider timeProvider,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        var codes = new CustomerVerificationCodeService(db, CreateConfig(), timeProvider);
        return new CustomerAuthService(
            db,
            tenantProvider,
            new FakeCustomerJwtIssuer(),
            new FakeViaCepClient(),
            new UnusedRegistrationFieldService(),
            phone,
            sendGate,
            codes,
            email,
            NullLogger<CustomerAuthService>.Instance,
            httpContextAccessor);
    }

    public static IConfiguration CreateConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Supabase:JwtSecret"] = JwtSecret,
            })
            .Build();

    public static CustomerAuthController CreateController(ICustomerAuthService auth) =>
        new(auth, new UnusedRegistrationFieldService(), new NoOpTenantBinder());

    public static RegisterCustomerRequestDto NewRegister(
        string? email = null,
        string? phone = null,
        string? document = null,
        string? name = null,
        string? password = null,
        CustomerType customerType = CustomerType.Individual) =>
        new()
        {
            Name = name ?? "Cliente Teste",
            Email = email ?? $"{Guid.NewGuid():N}@club.test",
            Password = password ?? "secret123",
            Phone = phone ?? "+5511999991111",
            CustomerType = customerType,
            Document = document ?? "529.982.247-25",
        };

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

internal sealed class FakeCustomerJwtIssuer : ICustomerJwtIssuer
{
    public string IssueToken(Customer customer) => "test-token";
}

internal sealed class FakeViaCepClient : IViaCepClient
{
    public Task<ViaCepAddress> LookupAsync(string postalCode, CancellationToken cancellationToken) =>
        Task.FromResult(new ViaCepAddress(postalCode, "Rua", "Bairro", "Cidade", "SP"));
}

internal sealed class UnusedRegistrationFieldService : IRegistrationFieldService
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

internal sealed class NoOpTenantBinder : IPublicTenantBinder
{
    public Task BindFromSubdomainAsync(string? subdomain, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

internal sealed class StubCustomerAuthService : ICustomerAuthService
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

    public Task<AuthResponseDto> VerifyEmailAsync(
        VerifyEmailRequestDto request,
        CancellationToken cancellationToken) =>
        Fail<AuthResponseDto>();

    public Task ResendVerificationAsync(
        ResendVerificationRequestDto request,
        CancellationToken cancellationToken) =>
        ToThrow is null ? Task.CompletedTask : Fail();

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
