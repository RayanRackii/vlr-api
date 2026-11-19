namespace Platform.Core.Domain.Enums;

/// <summary>
/// How an asset's weekly working hours constrain an allocation (resource planning).
/// Persisted as a string; hours are enforced by rejection, never as a warning.
/// </summary>
public enum AssetHoursPolicy
{
    /// <summary>Available around the clock; working hours are not evaluated.</summary>
    Anytime = 0,

    /// <summary>The work window must start inside working hours; it may finish after closing.</summary>
    StartWithinHours = 1,

    /// <summary>The whole work window must fall inside working hours.</summary>
    WithinHours = 2,
}
