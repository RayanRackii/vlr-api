namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record ResourceTypeCapabilitiesDto(
    bool Bookable,
    bool Deliverable,
    bool Maintenance);

public sealed record ResourceTypeFieldDto
{
    public required string Key { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? Label { get; init; }

    public bool Required { get; init; }

    public string? Visibility { get; init; }

    public bool Filterable { get; init; }

    public bool AskOnBooking { get; init; }

    public IReadOnlyList<string>? Options { get; init; }

    public string? ResponseType { get; init; }
}

public sealed record ResourceTypeResponse(
    Guid Id,
    string Key,
    string Name,
    string Singular,
    string Plural,
    string Article,
    bool Seeded,
    int ResourceCount,
    ResourceTypeCapabilitiesDto Capabilities,
    IReadOnlyList<ResourceTypeFieldDto> Fields);

public sealed record ResourceTypeWriteRequest
{
    public string? Key { get; init; }

    public required string Name { get; init; }

    public required string Singular { get; init; }

    public required string Plural { get; init; }

    public required string Article { get; init; }

    public required ResourceTypeCapabilitiesDto Capabilities { get; init; }

    public IReadOnlyList<ResourceTypeFieldDto>? Fields { get; init; }
}
