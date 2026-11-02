using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Validates that a planning request's buffered interval is an existing offerable booking window
/// for the target resource — never rounding (order-equipment-link-codex §3: "accepts only
/// buffered intervals expressible by existing booking windows and allowed durations"). Reuses
/// <see cref="OpenHoursRules.ValidateInterval"/>, the same narrow predicate
/// <c>ReservationService.CreateReservationAsync</c> already applies for OpenHours resources.
/// Phase 0 limitation: SlotGrid resources (weekly-template-derived bookable windows) are not
/// validated against persisted Slots/ScheduleTemplates here — that derivation is only reachable
/// through ReservationService's private slot-grid resolution, which is not practical to extract
/// generically within Phase 0's scope. A SlotGrid resource used for planning is only checked
/// against its <see cref="RentalAsset.AllowedDurationMinutes"/> (duration-only check); a later
/// phase should extract the full SlotGrid window predicate if production scheduling against
/// SlotGrid resources becomes a real requirement.
///
/// Consumed cross-module only through
/// <see cref="Platform.Api.Modules.Rentals.Contracts.IResourceOccupancyService.ValidateWorkWindowAsync"/>
/// (e.g. by Catalog's allocation/preview flows) — never referenced directly outside Rentals.
/// </summary>
internal static class ResourceWorkWindowValidator
{
    public const string UnsupportedWorkWindowCode =
        Platform.Api.Modules.Rentals.Contracts.WorkWindowValidation.UnsupportedWorkWindowCode;

    /// <summary>
    /// Returns null when [workStart, workEnd) (after prep/cleanup buffers are applied) is an
    /// expressible booking window for <paramref name="rental"/>; otherwise a human-readable reason.
    /// All timestamps must already be in <paramref name="zone"/>.
    /// </summary>
    public static string? Validate(
        RentalAsset rental,
        DateTimeOffset occupiedStart,
        DateTimeOffset occupiedEnd,
        TimeZoneInfo zone,
        string resourceName)
    {
        if (occupiedEnd <= occupiedStart)
        {
            return "The occupied interval must be positive.";
        }

        var localStart = TimeZoneInfo.ConvertTime(occupiedStart, zone);
        var localEnd = TimeZoneInfo.ConvertTime(occupiedEnd, zone);

        if (localStart.Date != localEnd.Date)
        {
            // Phase 0 scope is "one unit/day" (order-equipment-link-codex Phase 0 scope); a buffered
            // interval crossing midnight is not an expressible single-day booking window yet.
            return "Multi-day planning windows are not supported yet.";
        }

        var startTime = TimeOnly.FromDateTime(localStart.DateTime);
        var endTime = TimeOnly.FromDateTime(localEnd.DateTime);

        if (rental.SchedulePolicy == SchedulePolicy.OpenHours)
        {
            return OpenHoursRules.ValidateInterval(rental, startTime, endTime, resourceName);
        }

        // SlotGrid: Phase 0 simplification (documented above) — only an allowed-duration check,
        // not a full persisted-slot/template derivation.
        var minutes = (int)(occupiedEnd - occupiedStart).TotalMinutes;
        return OpenHoursRules.ParseDurations(rental.AllowedDurationMinutes).Any(allowed => allowed == minutes)
            ? null
            : $"'{resourceName}' requested duration is not an existing offerable booking window.";
    }
}
