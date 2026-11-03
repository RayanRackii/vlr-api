using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Grants a <see cref="CatalogProductVisibility.Restricted"/> product to exactly one target:
/// either a single <see cref="Customer"/> or a whole <see cref="CustomerGroup"/>.
/// A CHECK constraint enforces that exactly one of CustomerId/GroupId is set.
/// </summary>
public class CatalogProductAccess : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid ProductId { get; set; }

    /// <summary>Direct grant to a single customer. Mutually exclusive with <see cref="GroupId"/>.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Grant to a group. Mutually exclusive with <see cref="CustomerId"/>.</summary>
    public Guid? GroupId { get; set; }
}
