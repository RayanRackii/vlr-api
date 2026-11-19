using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.ResourcePlanning.Controllers;

/// <summary>
/// Manual allocations, conflicts and the planning agenda. Staff only; gated by the
/// <c>resource-planning</c> module alone. Creating is allowed to <c>allocations.manage</c> or
/// <c>allocations.execute</c> (a technician creates); editing needs <c>allocations.manage</c>;
/// the usage actions need either, and the service enforces the cancel rule (the creator or a
/// manager) because it depends on who created the allocation.
/// </summary>
[ApiController]
[RequireActiveModule(PlatformModules.ResourcePlanning)]
[Route("api/planning")]
public sealed class PlanningAllocationsController(
    IAssetAllocationService allocationService,
    IPlanningAgendaService agendaService) : ControllerBase
{
    [HttpGet("agenda")]
    [RequirePermission(Permissions.ResourcePlanning.AgendaRead)]
    public Task<ActionResult<PlanningAgendaResponse>> Agenda(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] Guid[]? assetIds,
        [FromQuery] Guid? unitId,
        CancellationToken cancellationToken) =>
        RunAsync(() => agendaService.GetAgendaAsync(from, to, assetIds, unitId, cancellationToken));

    [HttpPost("allocations/preview")]
    [RequirePermission(Permissions.ResourcePlanning.AgendaRead)]
    public Task<ActionResult<AllocationPreviewResponse>> Preview(
        [FromBody] CreateAssetAllocationRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(() => allocationService.PreviewAsync(request, cancellationToken));

    [HttpPost("allocations")]
    [RequireAnyPermission(
        Permissions.ResourcePlanning.AllocationsManage,
        Permissions.ResourcePlanning.AllocationsExecute)]
    public Task<ActionResult<AssetAllocationResponse>> Create(
        [FromBody] CreateAssetAllocationRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(() => allocationService.CreateAsync(request, cancellationToken));

    [HttpGet("allocations")]
    [RequirePermission(Permissions.ResourcePlanning.AgendaRead)]
    public Task<ActionResult<IReadOnlyList<AssetAllocationResponse>>> List(
        [FromQuery] Guid? assetId,
        [FromQuery] string? status,
        CancellationToken cancellationToken) =>
        RunAsync(() => allocationService.ListAsync(assetId, status, cancellationToken));

    [HttpGet("allocations/{id:guid}")]
    [RequirePermission(Permissions.ResourcePlanning.AgendaRead)]
    public Task<ActionResult<AssetAllocationResponse>> Get(Guid id, CancellationToken cancellationToken) =>
        RunAsync(() => allocationService.GetAsync(id, cancellationToken));

    [HttpPatch("allocations/{id:guid}")]
    [RequirePermission(Permissions.ResourcePlanning.AllocationsManage)]
    public Task<ActionResult<AssetAllocationResponse>> Update(
        Guid id,
        [FromBody] UpdateAssetAllocationRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(() => allocationService.UpdateAsync(id, request, cancellationToken));

    [HttpPost("allocations/{id:guid}/usage")]
    [RequireAnyPermission(
        Permissions.ResourcePlanning.AllocationsExecute,
        Permissions.ResourcePlanning.AllocationsManage)]
    public Task<ActionResult<AssetAllocationResponse>> Usage(
        Guid id,
        [FromBody] AssetAllocationUsageRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(() => allocationService.ApplyUsageAsync(id, request, cancellationToken));

    private async Task<ActionResult<T>> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (PlanningException ex)
        {
            return StatusCode(ex.StatusCode, ex.ToBody());
        }
    }
}
