using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;

namespace Platform.Api.Modules.ResourcePlanning;

/// <summary>
/// A resource-planning rule violation with the HTTP status and the stable error code the
/// controller returns as <c>{ error, code }</c> (plus <c>conflicts</c> or <c>hoursViolation</c>
/// when the rule carries detail).
/// </summary>
public sealed class PlanningException(
    int statusCode,
    string code,
    string message,
    IReadOnlyList<AllocationConflictDto>? conflicts = null,
    string? hoursViolation = null,
    string? module = null) : Exception(message)
{
    public const string AssetNotFoundCode = "planning.asset_not_found";
    public const string InvalidProfileCode = "planning.invalid_profile";
    public const string WorkingHoursRequiredCode = "planning.working_hours_required";
    public const string RentalScheduleAuthorityCode = "planning.rental_schedule_authority";
    public const string VersionConflictCode = "planning.version_conflict";
    public const string AllocationNotFoundCode = "planning.allocation_not_found";
    public const string InvalidAllocationCode = "planning.invalid_allocation";
    public const string AssetNotPlannableCode = "planning.asset_not_plannable";
    public const string ConflictCode = "planning.conflict";
    public const string OutsideWorkingHoursCode = "planning.outside_working_hours";
    public const string InvalidTransitionCode = "planning.invalid_transition";
    public const string OrderWorkflowRequiredCode = "planning.order_workflow_required";
    public const string UnavailableForbiddenCode = "planning.unavailable_forbidden";
    public const string CancelForbiddenCode = "planning.cancel_forbidden";
    public const string InvalidRangeCode = "planning.invalid_range";
    public const string UnsupportedWorkWindowCode = "planning.unsupported_work_window";

    /// <summary>
    /// The <c>hoursViolation</c> reason a preview reports when the rental schedule of a rental-backed
    /// asset rejects the window (same value as the rentals work-window validation code).
    /// </summary>
    public const string UnsupportedWorkWindowReason = "unsupported_work_window";

    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public IReadOnlyList<AllocationConflictDto>? Conflicts { get; } = conflicts;

    public string? HoursViolation { get; } = hoursViolation;

    /// <summary>The module key of a <c>module_inactive</c> error, as in the module gate body.</summary>
    public string? Module { get; } = module;

    public static PlanningException AssetNotFound(Guid assetId) =>
        new(StatusCodes.Status404NotFound, AssetNotFoundCode, $"Asset '{assetId}' was not found.");

    public static PlanningException Invalid(string message) =>
        new(StatusCodes.Status400BadRequest, InvalidProfileCode, message);

    public static PlanningException InvalidAllocation(string message) =>
        new(StatusCodes.Status400BadRequest, InvalidAllocationCode, message);

    public static PlanningException AllocationNotFound(Guid allocationId) =>
        new(StatusCodes.Status404NotFound, AllocationNotFoundCode, $"Allocation '{allocationId}' was not found.");

    public static PlanningException NotPlannable(string message) =>
        new(StatusCodes.Status409Conflict, AssetNotPlannableCode, message);

    public static PlanningException VersionConflict(string message) =>
        new(StatusCodes.Status409Conflict, VersionConflictCode, message);

    public static PlanningException InvalidTransition(string message) =>
        new(StatusCodes.Status409Conflict, InvalidTransitionCode, message);

    public static PlanningException Conflict(IReadOnlyList<AllocationConflictDto> conflicts) =>
        new(
            StatusCodes.Status409Conflict,
            ConflictCode,
            "The asset is already occupied in this window.",
            conflicts);

    /// <summary>
    /// A rental-backed asset keeps the rental schedule rules: the occupied window must be an
    /// offerable booking window within one day. 422, like an hours violation.
    /// </summary>
    public static PlanningException UnsupportedWorkWindow(string message) =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            UnsupportedWorkWindowCode,
            message,
            hoursViolation: UnsupportedWorkWindowReason);

    public static PlanningException OutsideWorkingHours(string reason) =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            OutsideWorkingHoursCode,
            reason == AssetWorkingHoursRules.StartOutsideHours
                ? "The work window must start inside the asset's working hours."
                : "The work window must fall inside the asset's working hours.",
            hoursViolation: reason);

    /// <summary>The JSON body the controller returns.</summary>
    public IReadOnlyDictionary<string, object?> ToBody()
    {
        var body = new Dictionary<string, object?>
        {
            ["error"] = Message,
            ["code"] = Code,
        };

        if (Conflicts is not null)
        {
            body["conflicts"] = Conflicts;
        }

        if (HoursViolation is not null)
        {
            body["hoursViolation"] = HoursViolation;
        }

        if (Module is not null)
        {
            body["module"] = Module;
        }

        return body;
    }
}
