using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Membership link between a <see cref="CustomerGroup"/> and a <see cref="Customer"/>.
/// Composite PK (GroupId, CustomerId); a customer may belong to many groups.
/// </summary>
public class CustomerGroupMember : ITenantScoped
{
    public required Guid TenantId { get; set; }
    public required Guid GroupId { get; set; }
    public required Guid CustomerId { get; set; }
}
