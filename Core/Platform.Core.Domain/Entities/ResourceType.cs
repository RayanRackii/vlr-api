using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Tenant resource type (a bookable family's vocabulary, capabilities, and custom fields).
/// Quadra and Caçamba are seeded. Schema: rentals.
/// </summary>
public class ResourceType : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required string Key { get; set; }

    public required string Name { get; set; }

    public required string Singular { get; set; }

    public required string Plural { get; set; }

    /// <summary><c>um</c> or <c>uma</c>.</summary>
    public required string Article { get; set; }

    public bool Seeded { get; set; }

    public bool Bookable { get; set; }

    public bool Deliverable { get; set; }

    public bool Maintenance { get; set; }

    public string FieldsJson { get; set; } = "[]";
}
