namespace Platform.Api.Modules.Assets.Dtos;

public sealed record AssetFamilyResponse(
    Guid Id,
    string Key,
    string Label,
    string FieldSchemaJson,
    int SortOrder,
    bool IsActive);

public sealed record AssetFamilyFieldDto(
    string Key,
    string Type,
    bool Required,
    string? Label);

public sealed record AssetFamilyDetailResponse(
    Guid Id,
    string Key,
    string Label,
    IReadOnlyList<AssetFamilyFieldDto> Fields,
    int SortOrder,
    bool IsActive);
