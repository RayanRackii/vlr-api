using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Assets.Controllers;

[ApiController]
[Route("api/asset-categories")]
public sealed class AssetCategoriesController(
    IAssetCategoryService assetCategoryService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Inventory.CategoriesRead)]
    public async Task<ActionResult<IReadOnlyList<AssetCategoryResponse>>> List(
        CancellationToken cancellationToken)
    {
        var categories = await assetCategoryService.ListAsync(cancellationToken);
        return Ok(categories);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.CategoriesRead)]
    public async Task<ActionResult<AssetCategoryResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var category = await assetCategoryService.GetByIdAsync(id, cancellationToken);

        if (category is null)
        {
            return NotFound();
        }

        return Ok(category);
    }

    [HttpPost]
    [RequirePermission(Permissions.Inventory.CategoriesWrite)]
    public async Task<ActionResult<AssetCategoryResponse>> Create(
        [FromBody] CreateAssetCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var category = await assetCategoryService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = category.Id }, category);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Inventory.CategoriesWrite)]
    public async Task<ActionResult<AssetCategoryResponse>> Update(
        Guid id,
        [FromBody] UpdateAssetCategoryRequest request,
        CancellationToken cancellationToken)
    {
        var category = await assetCategoryService.UpdateAsync(id, request, cancellationToken);

        if (category is null)
        {
            return NotFound();
        }

        return Ok(category);
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Inventory.CategoriesWrite)]
    public async Task<ActionResult<DeleteAssetCategoryResult>> Delete(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await assetCategoryService.DeleteAsync(id, cancellationToken);

            if (result is null)
            {
                return NotFound();
            }

            if (result.PermanentlyDeleted)
            {
                return NoContent();
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }
}
