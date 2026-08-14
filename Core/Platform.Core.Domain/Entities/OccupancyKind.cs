using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Tenant-defined occupancy kind for schedule cells (Open, Lesson, Closed, Event, …).
/// Schema: rentals.
/// </summary>
public class OccupancyKind : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    /// <summary>Stable slug unique per tenant (e.g. open, lesson, closed).</summary>
    public required string Key { get; set; }

    public required string Label { get; set; }

    public string? Description { get; set; }

    public string? ColorHex { get; set; }

    /// <summary>Optional Lucide icon key resolved by the client registry.</summary>
    public string? IconKey { get; set; }

    public required bool IsBookableByCustomer { get; set; }

    public required bool BlocksCapacity { get; set; }

    public required int SortOrder { get; set; }

    public required bool IsActive { get; set; }
}
