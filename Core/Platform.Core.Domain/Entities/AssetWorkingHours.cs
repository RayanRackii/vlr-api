using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// One weekly open window of a plannable asset, in wall-clock time of the tenant zone. Several
/// rows per day are allowed (lunch break). No rows with a policy other than Anytime means the
/// asset is closed all week.
/// </summary>
public class AssetWorkingHours : ITenantScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid TenantId { get; set; }

    public required Guid AssetId { get; set; }

    /// <summary>0 = Sunday ... 6 = Saturday (same numbering as <see cref="System.DayOfWeek"/>).</summary>
    public required short DayOfWeek { get; set; }

    public required TimeOnly OpenTime { get; set; }

    public required TimeOnly CloseTime { get; set; }
}
