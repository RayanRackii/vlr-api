namespace Platform.Core.Domain.Enums;

/// <summary>How bookable time is authored for a Rentable.</summary>
public enum SchedulePolicy
{
    /// <summary>Weekly templates materialize dated Slots (default).</summary>
    SlotGrid = 0,

    /// <summary>Continuous open/close window; bookable intervals are derived.</summary>
    OpenHours = 1
}
