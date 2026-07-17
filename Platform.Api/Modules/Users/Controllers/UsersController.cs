using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;

namespace Platform.Api.Modules.Users.Controllers;

[ApiController]
[Authorize]
[Route("api/users")]
public sealed class UsersController(
    IUserDirectoryService userDirectoryService) : ControllerBase
{
    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> GetCurrent(
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await userDirectoryService.GetCurrentAsync(User, cancellationToken);
            return Ok(user);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("technicians")]
    public async Task<ActionResult<IReadOnlyList<TechnicianUserResponse>>> ListTechnicians(
        CancellationToken cancellationToken)
    {
        var currentUser = await userDirectoryService.GetCurrentAsync(
            User,
            cancellationToken);

        if (currentUser.Role != ApplicationRoles.Admin)
        {
            return Forbid();
        }

        var technicians = await userDirectoryService.ListTechniciansAsync(cancellationToken);
        return Ok(technicians);
    }
}
