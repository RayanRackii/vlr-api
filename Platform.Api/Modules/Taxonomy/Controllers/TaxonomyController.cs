using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Platform.Api.Authorization;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Api.Modules.Taxonomy.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Taxonomy.Controllers;

public sealed class TaxonomyErrorsAttribute : ExceptionFilterAttribute, IActionFilter
{
    public TaxonomyErrorsAttribute() => Order = -3000; // Before ApiController's model-state filter.

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = new BadRequestObjectResult(new { error = "Invalid taxonomy request.", code = "TAXONOMY_INVALID" });
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is TaxonomyException ex)
        {
            context.Result = new ObjectResult(new { error = ex.Message, code = ex.Code }) { StatusCode = ex.Status };
            context.ExceptionHandled = true;
        }
        else if (context.Exception is UnauthorizedAccessException)
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Tenant context is required.", code = "TAXONOMY_INVALID" });
            context.ExceptionHandled = true;
        }
    }
}

[ApiController]
[TaxonomyErrors]
[Route("api/taxonomy")]
public sealed class TaxonomyController(TaxonomyService service) : ControllerBase
{
    [HttpGet("categories")]
    [RequirePermission(Permissions.Core.TaxonomyRead)]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> Categories(CancellationToken ct) => Ok(await service.CategoriesAsync(ct));

    [HttpGet("categories/{id:guid}")]
    [RequirePermission(Permissions.Core.TaxonomyRead)]
    public async Task<ActionResult<CategoryDto>> Category(Guid id, CancellationToken ct)
    {
        var result = await service.CategoryAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("categories")]
    [RequirePermission(Permissions.Core.TaxonomyManage)]
    public async Task<ActionResult<CategoryDto>> CreateCategory(CreateCategoryRequest request, CancellationToken ct)
    {
        var result = await service.CreateCategoryAsync(request, ct);
        return CreatedAtAction(nameof(Category), new { id = result.Id }, result);
    }

    [HttpPut("categories/{id:guid}")]
    [RequirePermission(Permissions.Core.TaxonomyManage)]
    public async Task<ActionResult<CategoryDto>> UpdateCategory(Guid id, UpdateCategoryRequest request, CancellationToken ct)
    {
        var result = await service.UpdateCategoryAsync(id, request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("categories/{id:guid}")]
    [RequirePermission(Permissions.Core.TaxonomyManage)]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct) =>
        await service.DeleteCategoryAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("tags")]
    [RequirePermission(Permissions.Core.TaxonomyRead)]
    public async Task<ActionResult<IReadOnlyList<TagDto>>> Tags(CancellationToken ct) => Ok(await service.TagsAsync(ct));

    [HttpGet("tags/{id:guid}")]
    [RequirePermission(Permissions.Core.TaxonomyRead)]
    public async Task<ActionResult<TagDto>> Tag(Guid id, CancellationToken ct)
    {
        var result = await service.TagAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("tags")]
    [RequirePermission(Permissions.Core.TaxonomyManage)]
    public async Task<ActionResult<TagDto>> CreateTag(CreateTagRequest request, CancellationToken ct)
    {
        var result = await service.CreateTagAsync(request, ct);
        return CreatedAtAction(nameof(Tag), new { id = result.Id }, result);
    }

    [HttpPut("tags/{id:guid}")]
    [RequirePermission(Permissions.Core.TaxonomyManage)]
    public async Task<ActionResult<TagDto>> UpdateTag(Guid id, UpdateTagRequest request, CancellationToken ct)
    {
        var result = await service.UpdateTagAsync(id, request, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpDelete("tags/{id:guid}")]
    [RequirePermission(Permissions.Core.TaxonomyManage)]
    public async Task<IActionResult> DeleteTag(Guid id, CancellationToken ct) =>
        await service.DeleteTagAsync(id, ct) ? NoContent() : NotFound();
}
