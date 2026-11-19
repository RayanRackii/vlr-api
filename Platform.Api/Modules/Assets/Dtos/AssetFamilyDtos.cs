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
    string? Label,
    string? Help = null,
    string? Unit = null,
    decimal? Min = null,
    decimal? Max = null,
    int? Decimals = null,
    IReadOnlyList<string>? Options = null,
    int? MaxLength = null,
    string? Group = null,
    bool Archived = false);

public sealed record AssetFamilyDetailResponse(
    Guid Id,
    string Key,
    string Label,
    IReadOnlyList<AssetFamilyFieldDto> Fields,
    int SortOrder,
    bool IsActive);

public sealed record CreateAssetFamilyRequest(
    string Key,
    string Label,
    IReadOnlyList<AssetFamilyFieldDto> Fields,
    int SortOrder,
    bool IsActive);

public sealed record UpdateAssetFamilyRequest(
    string Label,
    IReadOnlyList<AssetFamilyFieldDto> Fields,
    int SortOrder,
    bool IsActive,
    string? Key = null);
