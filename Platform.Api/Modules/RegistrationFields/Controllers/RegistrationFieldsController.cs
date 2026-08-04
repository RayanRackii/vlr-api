using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.RegistrationFields.Dtos;
using Platform.Api.Modules.RegistrationFields.Services;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.RegistrationFields.Controllers;

[ApiController]
[Route("api/registration-fields")]
[Authorize]
public sealed class RegistrationFieldsController(
    IRegistrationFieldService registrationFieldService,
    IUserDirectoryService userDirectoryService,
    ITenantProvider tenantProvider) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RegistrationFieldDto>>> List(
        CancellationToken cancellationToken)
    {
        await EnsureTenantAdminAsync(cancellationToken);
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var fields = await registrationFieldService.ListForTenantAsync(tenantId, cancellationToken);
        return Ok(fields);
    }

    [HttpPost]
    public async Task<ActionResult<RegistrationFieldDto>> Create(
        [FromBody] UpsertRegistrationFieldRequestDto request,
        CancellationToken cancellationToken)
    {
        await EnsureTenantAdminAsync(cancellationToken);
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
    public async Task<ActionResult<RegistrationFieldDto>> Update(
        Guid fieldId,
        [FromBody] UpdateRegistrationFieldRequestDto request,
        CancellationToken cancellationToken)
    {
        await EnsureTenantAdminAsync(cancellationToken);
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
    public async Task<IActionResult> Delete(
        Guid fieldId,
        CancellationToken cancellationToken)
    {
        await EnsureTenantAdminAsync(cancellationToken);
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

    private async Task EnsureTenantAdminAsync(CancellationToken cancellationToken)
    {
        var current = await userDirectoryService.GetCurrentAsync(User, cancellationToken);
        if (current.Role is not (ApplicationRoles.Admin or ApplicationRoles.SuperAdmin))
        {
            throw new UnauthorizedAccessException("Only tenant administrators can manage registration fields.");
        }
    }
}
