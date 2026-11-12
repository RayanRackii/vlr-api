using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/rentals/resource-types")]
public sealed class ResourceTypesController(IResourceTypeService resourceTypeService) : ControllerBase
{
    [HttpGet]
    [RequireAnyPermission(Permissions.Inventory.CategoriesRead, Permissions.Rentals.AssetsRead)]
    public async Task<ActionResult<IReadOnlyList<ResourceTypeResponse>>> List(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await resourceTypeService.ListAsync(cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpGet("{id:guid}")]
    [RequireAnyPermission(Permissions.Inventory.CategoriesRead, Permissions.Rentals.AssetsRead)]
    public async Task<ActionResult<ResourceTypeResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await resourceTypeService.GetAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpPost]
    [RequireAnyPermission(Permissions.Inventory.CategoriesWrite, Permissions.Rentals.AssetsWrite)]
    public async Task<ActionResult<ResourceTypeResponse>> Create(
        [FromBody] ResourceTypeWriteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await resourceTypeService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    [RequireAnyPermission(Permissions.Inventory.CategoriesWrite, Permissions.Rentals.AssetsWrite)]
    public async Task<ActionResult<ResourceTypeResponse>> Update(
        Guid id,
        [FromBody] ResourceTypeWriteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await resourceTypeService.UpdateAsync(id, request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}
