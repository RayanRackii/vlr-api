namespace Platform.Api.Modules.Admin.Dtos;

/// <summary>
/// What a module data purge would remove for one tenant, and why it cannot run yet.
/// Read-only; nothing is deleted by asking for it.
/// </summary>
/// <param name="Module">Canonical module key.</param>
/// <param name="ModuleActive">True while the module is still enabled for the tenant (a blocker).</param>
/// <param name="Tables">Tables that hold rows in scope, with their row count (empty tables are omitted).</param>
/// <param name="StorageObjects">Distinct storage keys referenced by rows in scope.</param>
/// <param name="Blockers">Reasons the purge would be refused. Empty means it could be scheduled.</param>
/// <param name="Pending">The purge already scheduled for this tenant and module, if any.</param>
public sealed record ModuleDataPurgePreviewDto(
    string Module,
    bool ModuleActive,
    IReadOnlyList<ModuleDataPurgeTableDto> Tables,
    long StorageObjects,
    IReadOnlyList<ModuleDataPurgeBlockerDto> Blockers,
    ModuleDataPurgePendingDto? Pending = null);

/// <param name="Table">Qualified table name, e.g. <c>rentals.reservations</c>.</param>
/// <param name="Rows">Rows of the tenant in that table that the purge would delete.</param>
public sealed record ModuleDataPurgeTableDto(string Table, long Rows);

/// <summary>
/// <c>module.active</c>, <c>fk.restrict</c> (rows outside the scope that reference it),
/// <c>fk.cascade</c> (rows outside the scope whose cascade would delete into another module's data)
/// or <c>allocations.unreleased</c>.
/// </summary>
public sealed record ModuleDataPurgeBlockerDto(string Code, string? Table, long? Rows);

/// <summary>A scheduled, not yet executed or cancelled purge.</summary>
public sealed record ModuleDataPurgePendingDto(Guid Id, DateTimeOffset ScheduledFor);

/// <summary>Body of <c>POST .../data-purge</c>: the module key typed by the admin.</summary>
public sealed record ScheduleModuleDataPurgeRequestDto(string? Confirm);

/// <summary>Result of scheduling a purge.</summary>
public sealed record ModuleDataPurgeScheduledDto(
    Guid Id,
    string Module,
    DateTimeOffset RequestedAt,
    DateTimeOffset ScheduledFor);
