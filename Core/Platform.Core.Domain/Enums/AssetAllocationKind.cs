namespace Platform.Core.Domain.Enums;

/// <summary>What an <see cref="Entities.AssetAllocation"/> represents.</summary>
public enum AssetAllocationKind
{
    /// <summary>The asset is used for a job.</summary>
    Use = 0,

    /// <summary>The asset is blocked (maintenance, downtime, holiday).</summary>
    Unavailable = 1,
}
