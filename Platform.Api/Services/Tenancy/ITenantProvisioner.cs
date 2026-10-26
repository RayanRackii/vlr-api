using Platform.Core.Domain.Entities;

namespace Platform.Api.Services.Tenancy;

/// <summary>
/// Seeds a new tenant. The caller owns validation, transaction boundaries,
/// access bootstrap and invitations, whose timing differs between admin and trial.
/// </summary>
public interface ITenantProvisioner
{
    Task<TenantProvisioningResult> ProvisionAsync(
        Tenant tenant,
        string unitName,
        string? unitCode,
        IReadOnlyList<string> modules,
        IReadOnlyList<Guid> familyIds,
        CancellationToken cancellationToken);
}

public sealed record TenantProvisioningResult(Unit Headquarters, Role? TrialSuperAdmin);
