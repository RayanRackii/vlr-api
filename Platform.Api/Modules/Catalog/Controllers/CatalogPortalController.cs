using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Exceptions;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/catalog/portal")]
public sealed class CatalogPortalController(
    ICatalogModuleGate catalogModuleGate,
    ICatalogPortalService portalService) : ControllerBase
{
    [HttpGet("products")]
    public async Task<ActionResult<IReadOnlyList<PortalProductResponse>>> ListProducts(
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var gate = await EnsureModuleAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        return Ok(await portalService.ListProductsAsync(search, cancellationToken));
    }

    [HttpGet("products/{id:guid}")]
    public async Task<ActionResult<PortalProductResponse>> GetProduct(
        Guid id,
        CancellationToken cancellationToken)
    {
        var gate = await EnsureModuleAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var product = await portalService.GetProductAsync(id, cancellationToken);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpPost("orders")]
    public async Task<ActionResult<CatalogOrderResponse>> CreateOrder(
        [FromBody] CreatePortalOrderRequest request,
        CancellationToken cancellationToken)
    {
        var gate = await EnsureModuleAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        try
        {
            var order = await portalService.CreateOrderAsync(
                ResolveCustomerId(),
                request,
                cancellationToken);
            return Ok(order);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpGet("orders")]
    public async Task<ActionResult<IReadOnlyList<CatalogOrderResponse>>> ListOrders(
        CancellationToken cancellationToken)
    {
        var gate = await EnsureModuleAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        return Ok(await portalService.ListOrdersAsync(ResolveCustomerId(), cancellationToken));
    }

    [HttpGet("orders/{id:guid}")]
    public async Task<ActionResult<CatalogOrderResponse>> GetOrder(
        Guid id,
        CancellationToken cancellationToken)
    {
        var gate = await EnsureModuleAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var order = await portalService.GetOrderAsync(ResolveCustomerId(), id, cancellationToken);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpPost("orders/{id:guid}/cancel")]
    public async Task<ActionResult<CatalogOrderResponse>> CancelOrder(
        Guid id,
        CancellationToken cancellationToken)
    {
        var gate = await EnsureModuleAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        try
        {
            return Ok(await portalService.CancelOrderAsync(ResolveCustomerId(), id, cancellationToken));
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

    [HttpPost("product-requests")]
    [RequestSizeLimit(CatalogFileRules.InternalB2BMaxBytes)]
    public async Task<ActionResult<ProductRequestResponse>> CreateProductRequest(
        [FromForm] string description,
        [FromForm] int quantity,
        [FromForm] string? note,
        [FromForm] IFormFileCollection? files,
        CancellationToken cancellationToken)
    {
        var gate = await EnsureModuleAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        try
        {
            var uploads = new List<PortalUpload>();
            if (files is not null)
            {
                foreach (var file in files)
                {
                    await using var stream = file.OpenReadStream();
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer, cancellationToken);
                    uploads.Add(new PortalUpload(
                        file.FileName,
                        file.ContentType ?? "application/octet-stream",
                        buffer.ToArray()));
                }
            }

            var created = await portalService.CreateProductRequestAsync(
                ResolveCustomerId(),
                new CreateProductRequestDto
                {
                    Description = description,
                    Quantity = quantity,
                    Note = note,
                },
                uploads,
                cancellationToken);
            return Ok(created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpGet("product-requests/{id:guid}/files/{fileId:guid}/url")]
    public async Task<ActionResult<CatalogFileUrlResponse>> GetProductRequestFileUrl(
        Guid id,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var gate = await EnsureModuleAsync(cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        try
        {
            return Ok(await portalService.GetOwnProductRequestFileUrlAsync(
                ResolveCustomerId(),
                id,
                fileId,
                cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    private async Task<ActionResult?> EnsureModuleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await catalogModuleGate.EnsureActiveAsync(cancellationToken);
            return null;
        }
        catch (CatalogModuleInactiveException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    private Guid ResolveCustomerId()
    {
        var customerIdClaim = User.FindFirst(CustomerClaimTypes.CustomerId)?.Value;
        if (!Guid.TryParse(customerIdClaim, out var customerId))
        {
            throw new UnauthorizedAccessException(
                "The access token is missing a valid customer_id claim.");
        }

        return customerId;
    }
}
