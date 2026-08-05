using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;

namespace Platform.Api.Modules.Admin.Controllers;

[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/tenants")]
public sealed class AdminTenantsController(IAdminTenantService adminTenantService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<TenantAdminResponseDto>>> List(
        CancellationToken cancellationToken)
    {
        var tenants = await adminTenantService.ListAsync(cancellationToken);
        return Ok(tenants);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TenantAdminResponseDto>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenant = await adminTenantService.GetByIdAsync(id, cancellationToken);

        if (tenant is null)
        {
            return NotFound(new { error = "Tenant not found." });
        }

        return Ok(tenant);
    }

    [HttpPost]
    public async Task<ActionResult<TenantAdminResponseDto>> Create(
        [FromBody] CreateTenantRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await adminTenantService.CreateAsync(request, cancellationToken);
            return Created($"/api/admin/tenants/{tenant.Id}", tenant);
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
    public async Task<ActionResult<TenantAdminResponseDto>> Update(
        Guid id,
        [FromBody] UpdateTenantRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenant = await adminTenantService.UpdateAsync(id, request, cancellationToken);
            return Ok(tenant);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
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

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await adminTenantService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    [HttpPost("{id:guid}/enter")]
    public async Task<ActionResult<EnterTenantEnvironmentResponseDto>> Enter(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await adminTenantService.EnterEnvironmentAsync(
                id,
                User,
                cancellationToken);
            return Ok(result);
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

    [HttpPost("exit")]
    public async Task<IActionResult> Exit(CancellationToken cancellationToken)
    {
        try
        {
            await adminTenantService.ExitEnvironmentAsync(User, cancellationToken);
            return NoContent();
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}
