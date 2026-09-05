using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Catalog)]
[Route("api/catalog")]
public sealed class CatalogOrdersController(ICatalogOrderService orderService) : ControllerBase
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
    public Task<ActionResult<CatalogOrderResponse>> Approve(
        Guid id,
        CancellationToken cancellationToken) =>
        ExecuteTransition(() => orderService.ApproveAsync(id, cancellationToken));

    [HttpPost("orders/{id:guid}/reject")]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    public Task<ActionResult<CatalogOrderResponse>> Reject(
        Guid id,
        [FromBody] CatalogReasonRequest request,
        CancellationToken cancellationToken) =>
        ExecuteTransition(() => orderService.RejectAsync(id, request.Reason, cancellationToken));

    [HttpPost("orders/{id:guid}/preparing")]
    [RequirePermission(Permissions.Catalog.OrdersManage)]
    public Task<ActionResult<CatalogOrderResponse>> Preparing(
        Guid id,
        CancellationToken cancellationToken) =>
        ExecuteTransition(() => orderService.StartPreparingAsync(id, cancellationToken));

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

    private async Task<ActionResult<CatalogOrderResponse>> ExecuteTransition(
        Func<Task<CatalogOrderResponse>> action)
    {
        try
        {
            return Ok(await action());
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
