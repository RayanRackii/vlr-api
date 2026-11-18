using Platform.Api.Modules.Taxonomy.Controllers;
using Platform.Api.Modules.Taxonomy.Dtos;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Storage;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[TaxonomyErrors]
[RequireActiveModule(PlatformModules.Catalog)]
[Route("api/catalog/products")]
public sealed class CatalogProductsController(
    ICatalogProductService productService,
    ICatalogProductResourceRequirementService requirementService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogProductResponse>>> List(
        [FromQuery] string? name,
        [FromQuery] string? code,
        [FromQuery] bool? isActive,
        CancellationToken cancellationToken,
        [FromQuery] string? category = null,
        [FromQuery] string? tags = null)
    {
        var products = await productService.ListAsync(
            new CatalogProductListQuery(name, code, isActive, TaxonomyFilter.Parse(category, tags)),
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

    /// <summary>Product change history, newest first. <c>before</c>/<c>beforeId</c> are the createdAt and id of the last item already loaded.</summary>
    [HttpGet("{id:guid}/history")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogProductHistoryEntryResponse>>> History(
        Guid id,
        [FromServices] ICatalogProductHistoryService historyService,
        CancellationToken cancellationToken,
        [FromQuery] int limit = CatalogProductHistoryService.DefaultLimit,
        [FromQuery] DateTimeOffset? before = null,
        [FromQuery] Guid? beforeId = null)
    {
        var entries = await historyService.ListAsync(id, limit, before, cancellationToken, beforeId);
        return entries is null ? NotFound() : Ok(entries);
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
        try
        {
            var product = await productService.ActivateAsync(id, cancellationToken);
            return product is null ? NotFound() : Ok(product);
        }
        catch (ProductNotReadyException ex)
        {
            return NotReady(ex);
        }
    }

    private ConflictObjectResult NotReady(ProductNotReadyException ex) =>
        Conflict(new { error = ex.Message, code = ProductNotReadyException.Code, missing = ex.Missing });

    /// <summary>
    /// Creates an inactive copy ("Cópia de ..."). Copies taxonomy, active resource requirements and
    /// access grants. Files (photos and documents) are NOT copied, because they live in storage.
    /// </summary>
    [HttpPost("{id:guid}/duplicate")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogProductResponse>> Duplicate(
        Guid id,
        [FromServices] ICatalogProductLifecycleService lifecycleService,
        CancellationToken cancellationToken)
    {
        var copyId = await lifecycleService.DuplicateAsync(id, cancellationToken);
        if (copyId is null) return NotFound();

        var copy = await productService.GetByIdAsync(copyId.Value, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = copyId.Value }, copy);
    }

    /// <summary>
    /// Deletes the product and its dependants. 409 <c>PRODUCT_HAS_ORDERS</c> when any order references it.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromServices] ICatalogProductLifecycleService lifecycleService,
        CancellationToken cancellationToken)
    {
        try
        {
            return await lifecycleService.DeleteAsync(id, cancellationToken) ? NoContent() : NotFound();
        }
        catch (ProductHasOrdersException ex)
        {
            return Conflict(new { error = ex.Message, code = ProductHasOrdersException.Code });
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
        catch (CatalogFileValidationException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
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

    // Which resource a product consumes is catalog configuration. Allocation preview, usage, and
    // approval that actually reserves a resource stay gated by resource-planning.
    [HttpGet("{id:guid}/resource-requirements")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogProductResourceRequirementsResponse>> GetResourceRequirements(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await requirementService.GetAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}/resource-requirements")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogProductResourceRequirementsResponse>> ReplaceResourceRequirements(
        Guid id,
        [FromBody] ReplaceCatalogProductResourceRequirementsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await requirementService.ReplaceAsync(id, request, cancellationToken));
        }
        catch (PlanningVersionConflictArgumentException ex)
        {
            return Conflict(new { error = ex.Message, code = PlanningVersionConflictArgumentException.Code });
        }
        catch (NotSupportedYetException ex)
        {
            return BadRequest(new { error = ex.Message, code = NotSupportedYetException.Code });
        }
        catch (UnsupportedRequirementScopeException ex)
        {
            return BadRequest(new { error = ex.Message, code = UnsupportedRequirementScopeException.Code });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
