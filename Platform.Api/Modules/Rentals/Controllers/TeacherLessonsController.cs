using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/schedule/lessons")]
public sealed class TeacherLessonsController(ITeacherLessonService teacherLessonService) : ControllerBase
{
    [RequirePermission(Permissions.Rentals.ScheduleLessonsWrite)]
    [HttpPost]
    public async Task<ActionResult<SlotResponseDto>> Create(
        [FromBody] CreateTeacherLessonRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await teacherLessonService.CreateAsync(request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
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

    [RequirePermission(Permissions.Rentals.ScheduleLessonsWrite)]
    [HttpPost("remove")]
    public async Task<ActionResult<SlotResponseDto>> Remove(
        [FromBody] RemoveTeacherLessonRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await teacherLessonService.RemoveAsync(request, cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
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
}
