using System.Globalization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Taxonomy.Dtos;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>Scalar product fields tracked by the "updated" history entry.</summary>
public readonly record struct CatalogProductHistoryFields(
    string Name,
    string? Code,
    string? Description,
    decimal? Price,
    string? Currency,
    bool UsesSchedule);

/// <summary>Pure helpers that turn before/after values into product history changes.</summary>
public static class CatalogProductHistoryDiff
{
    public static CatalogProductHistoryChange? Field(string field, string? oldValue, string? newValue) =>
        string.Equals(oldValue, newValue, StringComparison.Ordinal)
            ? null
            : new CatalogProductHistoryChange(field, oldValue, newValue);

    public static string? Money(decimal? value) =>
        value?.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Changed scalar fields of a product update; empty when nothing changed.</summary>
    public static List<CatalogProductHistoryChange> ForUpdate(
        CatalogProductHistoryFields before,
        CatalogProductHistoryFields after) =>
        Compact(
            Field("name", before.Name, after.Name),
            Field("code", before.Code, after.Code),
            Field("description", before.Description, after.Description),
            Field("price", Money(before.Price), Money(after.Price)),
            Field("currency", before.Currency, after.Currency),
            Field("usesSchedule", Bool(before.UsesSchedule), Bool(after.UsesSchedule)));

    /// <summary>Categories/tags changes by name; empty when the id sets are equal.</summary>
    public static List<CatalogProductHistoryChange> ForTaxonomy(
        IReadOnlyList<CategoryDto> oldCategories,
        IReadOnlyList<TagDto> oldTags,
        IReadOnlyList<CategoryDto> newCategories,
        IReadOnlyList<TagDto> newTags) =>
        Compact(
            oldCategories.Select(c => c.Id).ToHashSet().SetEquals(newCategories.Select(c => c.Id))
                ? null
                : new CatalogProductHistoryChange(
                    "categories",
                    Names(oldCategories.Select(c => c.Name)),
                    Names(newCategories.Select(c => c.Name))),
            oldTags.Select(t => t.Id).ToHashSet().SetEquals(newTags.Select(t => t.Id))
                ? null
                : new CatalogProductHistoryChange(
                    "tags",
                    Names(oldTags.Select(t => t.Name)),
                    Names(newTags.Select(t => t.Name))));

    public static string RequirementLine(string? assetName, int quantity, int durationMinutes, int prepMinutes, int cleanupMinutes) =>
        $"{assetName ?? "?"} x{quantity}, {durationMinutes}min, prep {prepMinutes}min, cleanup {cleanupMinutes}min";

    /// <summary>Resource requirements change; empty when the sets of lines are equal.</summary>
    public static List<CatalogProductHistoryChange> ForRequirements(IEnumerable<string> oldLines, IEnumerable<string> newLines)
    {
        var before = oldLines.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var after = newLines.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return before.SequenceEqual(after, StringComparer.Ordinal)
            ? []
            : [new CatalogProductHistoryChange("resourceRequirements", Join(before), Join(after))];
    }

    private static string? Join(List<string> lines) => lines.Count == 0 ? null : string.Join("; ", lines);

    private static string? Names(IEnumerable<string> names)
    {
        var list = names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    private static List<CatalogProductHistoryChange> Compact(params CatalogProductHistoryChange?[] changes) =>
        changes.OfType<CatalogProductHistoryChange>().ToList();
}
