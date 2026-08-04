namespace Platform.Api.Modules.RegistrationFields.Dtos;

public sealed record RegistrationFieldDto(
    Guid Id,
    string FieldKey,
    string Label,
    string FieldType,
    bool IsRequired,
    int SortOrder,
    IReadOnlyList<string>? Options);

public sealed record RegistrationSchemaResponseDto(
    IReadOnlyList<string> CoreFields,
    IReadOnlyList<RegistrationFieldDto> Fields);

public sealed record UpsertRegistrationFieldRequestDto
{
    public required string FieldKey { get; init; }

    public required string Label { get; init; }

    public required string FieldType { get; init; }

    public bool IsRequired { get; init; }

    public int SortOrder { get; init; }

    public IReadOnlyList<string>? Options { get; init; }
}

public sealed record UpdateRegistrationFieldRequestDto
{
    public required string Label { get; init; }

    public required string FieldType { get; init; }

    public bool IsRequired { get; init; }

    public int SortOrder { get; init; }

    public IReadOnlyList<string>? Options { get; init; }
}
