namespace Platform.Core.Infrastructure.MigrationOps;

public sealed record CatalogPreflightDuplicateSample(Guid TenantId, string MaskedCpf);

public sealed record CatalogPreflightDiagnosticCounts(
    int TotalCustomers,
    int CustomersWithCpf,
    int DuplicateGroupsWithinTenant,
    int DuplicateRowsWithinTenant,
    int NonDigitRows,
    int LengthNot11Rows,
    int InvalidCheckDigitRows,
    bool DocumentColumnPresent,
    int DocumentAlreadyPopulatedRows,
    int DocumentConflictWithCpfRows,
    int CatalogModuleActiveTenants,
    IReadOnlyList<CatalogPreflightDuplicateSample> DuplicateSamples);
