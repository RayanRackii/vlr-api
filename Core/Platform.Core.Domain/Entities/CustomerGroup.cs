using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// A tenant-defined grouping of customers, used to grant exclusive catalog access (and, later,
/// special pricing). Generic to any business: exclusivity, promotions, resale, agreements, etc.
/// </summary>
public class CustomerGroup : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    /// <summary>Display name. Unique per tenant.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    public void Rename(string name, string? description)
    {
        Name = name;
        Description = description;
        Touch();
    }
}
