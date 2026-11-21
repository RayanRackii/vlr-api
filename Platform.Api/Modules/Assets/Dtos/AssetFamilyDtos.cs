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
    bool Archived = false,
    string? LabelKey = null);

public sealed record AssetFamilyDetailResponse(
    Guid Id,
    string Key,
    string Label,
    IReadOnlyList<AssetFamilyFieldDto> Fields,
    int SortOrder,
    bool IsActive,
    int SchemaVersion = 1,
    string? LabelKey = null);

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
    string? Key = null,
    int? ExpectedSchemaVersion = null);

public sealed record AssetFamilySchemaPreviewRequest(
    IReadOnlyList<AssetFamilyFieldDto> Fields);

public sealed record AssetFamilySchemaPreviewResponse(
    int SchemaVersion,
    int AssetCount,
    int TenantCount,
    bool CanSave,
    IReadOnlyList<AssetFamilyFieldImpactDto> Fields);

public sealed record AssetFamilyFieldImpactDto(
    string Key,
    string Change,
    int AssetCount,
    int TenantCount,
    int IncompatibleCount,
    IReadOnlyList<string> IncompatibleSamples,
    int ConstraintViolationCount,
    bool Blocking);
