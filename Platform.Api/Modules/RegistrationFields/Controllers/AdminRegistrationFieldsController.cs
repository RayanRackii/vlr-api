using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.RegistrationFields.Dtos;
using Platform.Api.Modules.RegistrationFields.Services;

namespace Platform.Api.Modules.RegistrationFields.Controllers;

[ApiController]
[Route("api/admin/tenants/{tenantId:guid}/registration-fields")]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
public sealed class AdminRegistrationFieldsController(
    IRegistrationFieldService registrationFieldService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RegistrationFieldDto>>> List(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        try
        {
            var fields = await registrationFieldService.ListForTenantAsync(
                tenantId,
                cancellationToken);
            return Ok(fields);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpPost]
    public async Task<ActionResult<RegistrationFieldDto>> Create(
        Guid tenantId,
        [FromBody] UpsertRegistrationFieldRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await registrationFieldService.CreateAsync(
                tenantId,
                request,
                cancellationToken);
            return CreatedAtAction(nameof(List), new { tenantId }, created);
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
    }

    [HttpPut("{fieldId:guid}")]
    public async Task<ActionResult<RegistrationFieldDto>> Update(
        Guid tenantId,
        Guid fieldId,
        [FromBody] UpdateRegistrationFieldRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await registrationFieldService.UpdateAsync(
                tenantId,
                fieldId,
                request,
                cancellationToken);
            return Ok(updated);
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

    [HttpDelete("{fieldId:guid}")]
    public async Task<IActionResult> Delete(
        Guid tenantId,
        Guid fieldId,
        CancellationToken cancellationToken)
    {
        try
        {
            await registrationFieldService.DeleteAsync(tenantId, fieldId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
