using Platform.Api.Services.Brazil;
using Platform.Core.Domain.Services;
using Platform.Core.Infrastructure.MigrationOps;

namespace Platform.Api.Tests.MigrationOps;

public sealed class CatalogPreflightDiagnosticsTests
{
    [Fact]
    public void Aggregate_counts_duplicates_length_and_check_digits_per_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var rows = new List<(Guid TenantId, string? Cpf)>
        {
            (tenantA, "52998224725"),
            (tenantA, "52998224725"),
            (tenantB, "52998224725"),
            (tenantA, "11111111111"),
            (tenantA, "abc"),
            (tenantA, "123"),
            (tenantA, null),
        };

        var counts = CatalogPreflightDiagnostics.Aggregate(
            rows,
            catalogActiveTenants: 0,
            documentColumnPresent: false,
            documentPopulated: 0,
            documentConflicts: 0);

        Assert.Equal(7, counts.TotalCustomers);
        Assert.Equal(6, counts.CustomersWithCpf);
        Assert.Equal(1, counts.DuplicateGroupsWithinTenant);
        Assert.Equal(2, counts.DuplicateRowsWithinTenant);
        Assert.Equal(1, counts.NonDigitRows);
        Assert.Equal(2, counts.LengthNot11Rows);
        Assert.Equal(1, counts.InvalidCheckDigitRows);
        Assert.False(counts.DocumentColumnPresent);
        Assert.Equal("***.***.***-25", counts.DuplicateSamples[0].MaskedCpf);
        Assert.DoesNotContain("52998224725", counts.DuplicateSamples[0].MaskedCpf);
    }

    [Fact]
    public void Check_digits_match_registration_validator()
    {
        Assert.Equal(
            BrazilianDocumentValidator.IsValidCpfDigits("52998224725"),
            BrazilianCpf.IsValidCheckDigits("52998224725"));
        Assert.Equal(
            BrazilianDocumentValidator.IsValidCpfDigits("11111111111"),
            BrazilianCpf.IsValidCheckDigits("11111111111"));
        Assert.Equal(
            BrazilianDocumentValidator.IsValidCpfDigits("12345678901"),
            BrazilianCpf.IsValidCheckDigits("12345678901"));
    }
}
