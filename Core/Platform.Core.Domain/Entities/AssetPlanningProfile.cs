using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Resource-planning profile of one <see cref="Asset"/>: the opt-in flag, capacity, hours policy
/// and default buffers. One row per asset and tenant. Presence plus <see cref="IsPlannable"/> is
/// the opt-in; an asset without a row is not plannable. Weekly hours are
/// <see cref="AssetWorkingHours"/> rows.
/// </summary>
public class AssetPlanningProfile : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid AssetId { get; set; }

    public bool IsPlannable { get; set; } = true;

    /// <summary>Concurrent allocations the asset can hold. Phase 1 UI fixes this to 1.</summary>
    public int Capacity { get; set; } = 1;

    public AssetHoursPolicy HoursPolicy { get; set; } = AssetHoursPolicy.StartWithinHours;

    public int DefaultPrepMinutes { get; set; }

    public int DefaultCleanupMinutes { get; set; }
}
