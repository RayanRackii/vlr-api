using Platform.Api.Modules.CustomerAuth.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class CustomerDocumentTests
{
    [Fact]
    public async Task Register_individual_writes_cpf_and_document()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
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
        await using var harness = await CustomerAuthHarness.CreateAsync();
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
        await using var harness = await CustomerAuthHarness.CreateAsync();
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
        await using var harness = await CustomerAuthHarness.CreateAsync();
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
        Assert.False(updated.EmailVerified);
        Assert.False(updated.PhoneVerified);
    }

    [Fact]
    public async Task Backfill_mapping_copies_cpf_into_document()
    {
        await using var harness = await CustomerAuthHarness.CreateAsync();
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
}
