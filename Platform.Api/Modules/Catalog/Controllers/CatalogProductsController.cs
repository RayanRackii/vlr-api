using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Storage;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Catalog)]
[Route("api/catalog/products")]
public sealed class CatalogProductsController(ICatalogProductService productService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogProductResponse>>> List(
        [FromQuery] string? name,
        [FromQuery] string? code,
        [FromQuery] bool? isActive,
        CancellationToken cancellationToken)
    {
        var products = await productService.ListAsync(
            new CatalogProductListQuery(name, code, isActive),
            cancellationToken);
        return Ok(products);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<CatalogProductResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var product = await productService.GetByIdAsync(id, cancellationToken);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpPost]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogProductResponse>> Create(
        [FromBody] CreateCatalogProductRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var product = await productService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = product.Id }, product);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogProductResponse>> Update(
        Guid id,
        [FromBody] UpdateCatalogProductRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var product = await productService.UpdateAsync(id, request, cancellationToken);
            return product is null ? NotFound() : Ok(product);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/deactivate")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogProductResponse>> Deactivate(
        Guid id,
        CancellationToken cancellationToken)
    {
        var product = await productService.DeactivateAsync(id, cancellationToken);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpPost("{id:guid}/activate")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogProductResponse>> Activate(
        Guid id,
        CancellationToken cancellationToken)
    {
        var product = await productService.ActivateAsync(id, cancellationToken);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpPost("{id:guid}/files")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    [RequestSizeLimit(CatalogFileRules.InternalB2BMaxBytes)]
    public async Task<ActionResult<CatalogProductFileDto>> AddFile(
        Guid id,
        IFormFile file,
        [FromForm] CatalogFileVisibility visibility,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "File is required." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var dto = await productService.AddFileAsync(
                id,
                file.FileName,
                file.ContentType ?? "application/octet-stream",
                stream,
                visibility,
                cancellationToken);
            return Ok(dto);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (StorageProviderException ex)
        {
            return StorageProviderActionResults.From(ex);
        }
        catch (InvalidOperationException ex)
        {
            return StorageProviderActionResults.FromInvalidOperation(ex);
        }
        catch (HttpRequestException)
        {
            return StorageProviderActionResults.FromHttpRequestException();
        }
    }

    [HttpDelete("{id:guid}/files/{fileId:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<IActionResult> DeleteFile(
        Guid id,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            await productService.DeleteFileAsync(id, fileId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}/files/{fileId:guid}/url")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogFileUrlResponse>> GetFileUrl(
        Guid id,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = await productService.GetFileUrlAsync(id, fileId, cancellationToken);
            return Ok(url);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (StorageProviderException ex)
        {
            return StorageProviderActionResults.From(ex);
        }
        catch (InvalidOperationException ex)
        {
            return StorageProviderActionResults.FromInvalidOperation(ex);
        }
        catch (HttpRequestException)
        {
            return StorageProviderActionResults.FromHttpRequestException();
        }
    }
}
