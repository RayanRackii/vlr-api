namespace Platform.Api.Modules.ResourcePlanning.Dtos;

/// <summary>
/// One row of <c>GET /api/planning/assets</c>: an asset of the tenant with its planning opt-in.
/// This is the authorized read path of the asset registry for resource planning when the
/// inventory module is not entitled.
/// </summary>
public sealed record PlanningAssetResponse(
    Guid Id,
    string Name,
    string Tag,
    Guid CategoryId,
    string CategoryName,
    Guid UnitId,
    string UnitName,
    string Status,
    bool IsPlannable,
    bool IsRentalBacked,
    string? HoursPolicy,
    int WorkingWindowCount);

/// <summary>
/// One weekly open window in wall-clock time of the tenant zone. <c>DayOfWeek</c> is 0 (Sunday)
/// to 6 (Saturday); times are <c>HH:mm</c>. A close time of 23:59 means "until midnight".
/// </summary>
public sealed record AssetWorkingHoursWindowDto
{
    public int DayOfWeek { get; init; }

    public string OpenTime { get; init; } = string.Empty;

    public string CloseTime { get; init; } = string.Empty;
}

/// <summary>
/// Planning profile of an asset plus its weekly hours. <c>IsConfigured</c> is false when the
/// asset has never been configured; the response then carries the defaults (not plannable).
/// </summary>
public sealed record AssetPlanningProfileResponse(
    Guid AssetId,
    bool IsConfigured,
    bool IsPlannable,
    bool IsRentalBacked,
    int Capacity,
    string HoursPolicy,
    int DefaultPrepMinutes,
    int DefaultCleanupMinutes,
    IReadOnlyList<AssetWorkingHoursWindowDto> WorkingHours);

/// <summary>
/// Upserts the profile and replaces the whole weekly hours list atomically.
/// <c>HoursPolicy</c> is <c>Anytime</c>, <c>StartWithinHours</c> (default) or <c>WithinHours</c>.
/// </summary>
public sealed record PutAssetPlanningProfileRequest
{
    public bool IsPlannable { get; init; }

    public int Capacity { get; init; } = 1;

    public string HoursPolicy { get; init; } = "StartWithinHours";

    public int DefaultPrepMinutes { get; init; }

    public int DefaultCleanupMinutes { get; init; }

    public IReadOnlyList<AssetWorkingHoursWindowDto> WorkingHours { get; init; } = [];
}
