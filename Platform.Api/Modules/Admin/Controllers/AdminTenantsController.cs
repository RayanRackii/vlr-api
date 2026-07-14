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
}
