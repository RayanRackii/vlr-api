namespace Platform.Api.Modules.ResourcePlanning;

/// <summary>
/// A resource-planning rule violation with the HTTP status and the stable error code the
/// controller returns as <c>{ error, code }</c>.
/// </summary>
public sealed class PlanningException(int statusCode, string code, string message) : Exception(message)
{
    public const string AssetNotFoundCode = "planning.asset_not_found";
    public const string InvalidProfileCode = "planning.invalid_profile";
    public const string WorkingHoursRequiredCode = "planning.working_hours_required";
    public const string RentalScheduleAuthorityCode = "planning.rental_schedule_authority";
    public const string VersionConflictCode = "planning.version_conflict";

    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public static PlanningException AssetNotFound(Guid assetId) =>
        new(StatusCodes.Status404NotFound, AssetNotFoundCode, $"Asset '{assetId}' was not found.");

    public static PlanningException Invalid(string message) =>
        new(StatusCodes.Status400BadRequest, InvalidProfileCode, message);
}
