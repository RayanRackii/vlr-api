using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Api.Modules.Taxonomy.Dtos;

public sealed record CategoryDto(Guid Id, string Name, string Slug, Guid? ParentId, string? Icon, int SortOrder);
public sealed record TagDto(Guid Id, string Name, string Slug);
public sealed record TaxonomyResponse(IReadOnlyList<CategoryDto> Categories, IReadOnlyList<TagDto> Tags)
{
    public static TaxonomyResponse Empty { get; } = new([], []);
}
public sealed record CreateCategoryRequest(string Name, string Slug, Guid? ParentId = null, string? Icon = null, int SortOrder = 0);
public sealed record UpdateCategoryRequest(string Name, Guid? ParentId, string? Icon, int SortOrder)
{
    // Capture legacy/unknown input so an attempted slug change cannot be silently ignored.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
public sealed record CreateTagRequest(string Name, string Slug);
public sealed record UpdateTagRequest(string Name)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
public sealed record TaxonomyAssignmentRequest
{
    public Guid[]? CategoryIds { get; init; }
    public Guid[]? TagIds { get; init; }
}
