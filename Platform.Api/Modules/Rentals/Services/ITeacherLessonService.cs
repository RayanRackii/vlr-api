using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface ITeacherLessonService
{
    Task<SlotResponseDto> CreateAsync(
        CreateTeacherLessonRequestDto request,
        CancellationToken cancellationToken);

    Task<SlotResponseDto> RemoveAsync(
        RemoveTeacherLessonRequestDto request,
        CancellationToken cancellationToken);
}
