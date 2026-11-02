using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Catalog)]
[Route("api/catalog")]
public sealed class CatalogOrdersController(
    ICatalogOrderService orderService,
    ICatalogOrderAllocationService allocationService,
    ICatalogOrderAllocationPreviewService previewService,
    ICatalogOrderAllocationUsageService usageService,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("product-requests")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<IReadOnlyList<ProductRequestResponse>>> ListProductRequests(
        CancellationToken cancellationToken)
    {
        return Ok(await orderService.ListProductRequestsAsync(cancellationToken));
    }

    [HttpGet("product-requests/{id:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<ProductRequestResponse>> GetProductRequest(
        Guid id,
        CancellationToken cancellationToken)
    {
        var request = await orderService.GetProductRequestAsync(id, cancellationToken);
        return request is null ? NotFound() : Ok(request);
    }

    [HttpGet("orders")]
    [RequirePermission(Permissions.Catalog.OrdersRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogOrderResponse>>> ListOrders(
        [FromQuery] int? orderNumber,
        [FromQuery] CatalogOrderStatus? status,
        [FromQuery] Guid? customerId,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var orders = await orderService.ListAsync(
            new CatalogOrderListQuery(orderNumber, status, customerId, from, to),
            cancellationToken);
        return Ok(orders);
    }

    [HttpGet("orders/{id:guid}")]
    [RequirePermission(Permissions.Catalog.OrdersRead)]
    public async Task<ActionResult<CatalogOrderResponse>> GetOrder(
        Guid id,
        CancellationToken cancellationToken)
    {
        var order = await orderService.GetByIdAsync(id, cancellationToken);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpPost("orders/{id:guid}/approve")]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    public async Task<ActionResult<CatalogOrderResponse>> Approve(
        Guid id,
        [FromBody] CatalogOrderApprovePlanningRequest? request,
        CancellationToken cancellationToken)
    {
        if (await RequiresAllocatePermissionButLacksItAsync(id, cancellationToken))
        {
            return Forbid();
        }

        return await ExecuteTransition(() => orderService.ApproveAsync(id, request, cancellationToken));
    }

    /// <summary>Read-only; reserves nothing, holds no lock. A preview is never a booking guarantee.</summary>
    [HttpPost("orders/{id:guid}/allocation-preview")]
    [RequireActiveModule(PlatformModules.ResourcePlanning)]
    [RequirePermission(Permissions.Catalog.OrdersRead)]
    public async Task<ActionResult<CatalogOrderAllocationPreviewResponse>> AllocationPreview(
        Guid id,
        [FromBody] CatalogOrderAllocationPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var canReadSchedule = await authorizationService.AuthorizeAsync(
            User, PermissionPolicies.Name(Permissions.Rentals.ScheduleRead));
        if (!canReadSchedule.Succeeded)
        {
            return Forbid();
        }

        try
        {
            return Ok(await previewService.PreviewAsync(id, request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("orders/{id:guid}/allocations/{allocationId:guid}/usage")]
    [RequireActiveModule(PlatformModules.ResourcePlanning)]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    [RequirePermission(Permissions.Catalog.OrdersAllocate)]
    public async Task<ActionResult<CatalogOrderResponse>> AllocationUsage(
        Guid id,
        Guid allocationId,
        [FromBody] CatalogOrderAllocationUsageRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await usageService.ApplyUsageActionAsync(id, allocationId, request, cancellationToken));
        }
        catch (PlanningVersionConflictException ex)
        {
            return Conflict(new { error = ex.Message, code = PlanningVersionConflictException.Code });
        }
        catch (InvalidCatalogOrderTransitionException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost("orders/{id:guid}/reject")]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    public Task<ActionResult<CatalogOrderResponse>> Reject(
        Guid id,
        [FromBody] CatalogReasonRequest request,
        CancellationToken cancellationToken) =>
        ExecuteTransition(() => orderService.RejectAsync(id, request.Reason, cancellationToken));

    [HttpPost("orders/{id:guid}/preparing")]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    public async Task<ActionResult<CatalogOrderResponse>> Preparing(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (await RequiresAllocatePermissionButLacksItAsync(id, cancellationToken))
        {
            return Forbid();
        }

        return await ExecuteTransition(() => orderService.StartPreparingAsync(id, cancellationToken));
    }

    [HttpPost("orders/{id:guid}/ready")]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    public Task<ActionResult<CatalogOrderResponse>> Ready(
        Guid id,
        CancellationToken cancellationToken) =>
        ExecuteTransition(() => orderService.MarkReadyAsync(id, cancellationToken));

    [HttpPost("orders/{id:guid}/complete")]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    public Task<ActionResult<CatalogOrderResponse>> Complete(
        Guid id,
        CancellationToken cancellationToken) =>
        ExecuteTransition(() => orderService.CompleteAsync(id, cancellationToken));

    [HttpPost("orders/{id:guid}/cancel")]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    public Task<ActionResult<CatalogOrderResponse>> Cancel(
        Guid id,
        [FromBody] CatalogReasonRequest request,
        CancellationToken cancellationToken) =>
        ExecuteTransition(() => orderService.CancelAsync(id, request.Reason, cancellationToken));

    /// <summary>
    /// H2: orders.manage alone must not be sufficient to approve/start-production an order whose
    /// transition actually allocates or starts a resource — only orders with a schedulable
    /// requirement require the extra catalog.orders.allocate permission; resource-free orders keep
    /// their pre-Phase-0 permission set unchanged.
    /// </summary>
    private async Task<bool> RequiresAllocatePermissionButLacksItAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (!await allocationService.HasSchedulableRequirementAsync(orderId, cancellationToken))
        {
            return false;
        }

        var canAllocate = await authorizationService.AuthorizeAsync(
            User, PermissionPolicies.Name(Permissions.Catalog.OrdersAllocate));
        return !canAllocate.Succeeded;
    }

    private async Task<ActionResult<CatalogOrderResponse>> ExecuteTransition(
        Func<Task<CatalogOrderResponse>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (PlanningVersionConflictException ex)
        {
            return Conflict(new { error = ex.Message, code = PlanningVersionConflictException.Code });
        }
        catch (UnsupportedRequirementScopeException ex)
        {
            return Conflict(new { error = ex.Message, code = UnsupportedRequirementScopeException.Code });
        }
        catch (ResourceUnavailableException ex)
        {
            return Conflict(new { error = ex.Message, code = ResourceUnavailableException.Code });
        }
        catch (UnsupportedWorkWindowException ex)
        {
            return BadRequest(new { error = ex.Message, code = UnsupportedWorkWindowException.Code });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidCatalogOrderTransitionException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
