using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.RegistrationFields.Dtos;
using Platform.Api.Modules.RegistrationFields.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.RegistrationFields.Controllers;

[ApiController]
[Route("api/registration-fields")]
public sealed class RegistrationFieldsController(
    IRegistrationFieldService registrationFieldService,
    ITenantProvider tenantProvider) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Core.RegistrationFieldsRead)]
    public async Task<ActionResult<IReadOnlyList<RegistrationFieldDto>>> List(
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var fields = await registrationFieldService.ListForTenantAsync(tenantId, cancellationToken);
        return Ok(fields);
    }

    [HttpPost]
    [RequirePermission(Permissions.Core.RegistrationFieldsWrite)]
    public async Task<ActionResult<RegistrationFieldDto>> Create(
        [FromBody] UpsertRegistrationFieldRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        try
        {
            var created = await registrationFieldService.CreateAsync(
                tenantId,
                request,
                cancellationToken);
            return CreatedAtAction(nameof(List), new { id = created.Id }, created);
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

    [HttpPut("{fieldId:guid}")]
    [RequirePermission(Permissions.Core.RegistrationFieldsWrite)]
    public async Task<ActionResult<RegistrationFieldDto>> Update(
        Guid fieldId,
        [FromBody] UpdateRegistrationFieldRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

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
    [RequirePermission(Permissions.Core.RegistrationFieldsWrite)]
    public async Task<IActionResult> Delete(
        Guid fieldId,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

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
