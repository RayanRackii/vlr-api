namespace Platform.Api.Modules.Admin.Dtos;

public sealed record AdminModuleCatalogItemDto(
    string Key,
    bool IsCommercial,
    bool IsLegacy,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> Aliases);
