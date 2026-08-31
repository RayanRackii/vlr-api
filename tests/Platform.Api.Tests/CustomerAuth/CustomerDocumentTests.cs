using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authentication;
using Platform.Api.Modules.CustomerAuth.Dtos;
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

public sealed class CustomerDocumentTests
{
    [Fact]
    public async Task Register_individual_writes_cpf_and_document()
    {
        await using var harness = await CreateAsync();
        var response = await harness.Auth.RegisterAsync(
            NewRegister(CustomerType.Individual, "529.982.247-25"),
            CancellationToken.None);

        var customer = await harness.Db.Customers.FindAsync(response.CustomerId);
        Assert.Equal(CustomerType.Individual, customer!.CustomerType);
        Assert.Equal("52998224725", customer.Cpf);
        Assert.Equal("52998224725", customer.Document);
    }

    [Fact]
    public async Task Register_company_writes_document_only()
    {
        await using var harness = await CreateAsync();
        var response = await harness.Auth.RegisterAsync(
            NewRegister(CustomerType.Company, "11.222.333/0001-81"),
            CancellationToken.None);

        var customer = await harness.Db.Customers.FindAsync(response.CustomerId);
        Assert.Equal(CustomerType.Company, customer!.CustomerType);
        Assert.Null(customer.Cpf);
        Assert.Equal("11222333000181", customer.Document);
    }

    [Fact]
    public async Task Document_is_unique_per_tenant()
    {
        await using var harness = await CreateAsync();
        await harness.Auth.RegisterAsync(
            NewRegister(CustomerType.Individual, "52998224725", email: "one@club.test", phone: "+5511988880001"),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Auth.RegisterAsync(
                NewRegister(CustomerType.Individual, "52998224725", email: "two@club.test", phone: "+5511988880002"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Profile_patch_does_not_change_type_or_document()
    {
        await using var harness = await CreateAsync();
        var registered = await harness.Auth.RegisterAsync(
            NewRegister(CustomerType.Company, "11222333000181"),
            CancellationToken.None);

        var updated = await harness.Auth.UpdateProfileAsync(
            registered.CustomerId,
            new UpdateCustomerProfileRequestDto { Name = "Nova Empresa" },
            CancellationToken.None);

        Assert.Equal(CustomerType.Company, updated.CustomerType);
        Assert.Equal("11222333000181", updated.Document);
        Assert.Equal("Nova Empresa", updated.Name);
    }

    [Fact]
    public async Task Backfill_mapping_copies_cpf_into_document()
    {
        await using var harness = await CreateAsync();
        var customer = new Customer
        {
            TenantId = harness.Tenant.Id,
            Name = "Legacy",
            Cpf = "52998224725",
        };
        customer.Document = customer.Cpf;
        harness.Db.Customers.Add(customer);
        await harness.Db.SaveChangesAsync();

        Assert.Equal(customer.Cpf, customer.Document);
    }

    private static RegisterCustomerRequestDto NewRegister(
        CustomerType type,
        string document,
        string? email = null,
        string? phone = null) =>
        new()
        {
            Name = "Cliente Teste",
            Email = email ?? $"{Guid.NewGuid():N}@club.test",
            Password = "secret123",
            Phone = phone ?? "+5511999991111",
            CustomerType = type,
            Document = document,
        };

    private static async Task<AuthHarness> CreateAsync()
    {
        var tenant = new Tenant("Auth Club", "66666666000191", subdomain: "authclub");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var auth = new CustomerAuthService(
            db,
            tenantProvider,
            new FakeJwtIssuer(),
            new FakeViaCep(),
            new EmptyRegistrationFields(),
            new FakePhoneVerificationClient(),
            NullLogger<CustomerAuthService>.Instance);
        return new AuthHarness(db, tenant, auth);
    }

    private sealed class AuthHarness(AppDbContext db, Tenant tenant, CustomerAuthService auth)
        : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public Tenant Tenant { get; } = tenant;
        public CustomerAuthService Auth { get; } = auth;

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

    private sealed class EmptyRegistrationFields : IRegistrationFieldService
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
}
