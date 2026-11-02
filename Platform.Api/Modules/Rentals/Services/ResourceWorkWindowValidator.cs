using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Validates that a planning request's buffered interval is an existing offerable booking window
/// for the target resource — never rounding (order-equipment-link-codex §3: "accepts only
/// buffered intervals expressible by existing booking windows and allowed durations"). Reuses
/// <see cref="OpenHoursRules.ValidateInterval"/> for OpenHours resources and
/// <see cref="SlotGridWindowResolver"/> for SlotGrid resources — the same predicates
/// <c>ReservationService.CreateReservationAsync</c> applies for a customer booking, so planning
/// can never approve a window booking itself would reject (M1: dated overrides, cancelled cells
/// and persisted-slot boundaries all apply here exactly as they do for a customer booking).
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
    /// All timestamps must already be in <paramref name="zone"/>. Must run under the caller's
    /// resource lock (the same convention <see cref="ResourceOccupancyService"/> already requires
    /// for availability checks) so the calendar it reads cannot change underneath it.
    /// </summary>
    public static async Task<string?> ValidateAsync(
        AppDbContext dbContext,
        Guid tenantId,
        RentalAsset rental,
        DateTimeOffset occupiedStart,
        DateTimeOffset occupiedEnd,
        TimeZoneInfo zone,
        string resourceName,
        CancellationToken cancellationToken)
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

        // SlotGrid: the same persisted-Slot/ScheduleTemplate derivation booking itself uses,
        // including dated overrides, cancelled cells and exact slot boundaries (M1).
        var date = DateOnly.FromDateTime(localStart.DateTime);
        var plan = await SlotGridWindowResolver.ResolveAsync(
            dbContext, tenantId, rental, date, startTime, endTime, cancellationToken);

        return plan.IsOpen
            ? null
            : $"'{resourceName}' requested interval is not an existing offerable booking window.";
    }
}
