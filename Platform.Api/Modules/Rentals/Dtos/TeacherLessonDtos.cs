namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record CreateTeacherLessonRequestDto
{
    public required Guid RentalAssetId { get; init; }

    public required DateOnly Date { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public string? Label { get; init; }
}

public sealed record RemoveTeacherLessonRequestDto
{
    public required Guid RentalAssetId { get; init; }

    public required DateOnly Date { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }
}
