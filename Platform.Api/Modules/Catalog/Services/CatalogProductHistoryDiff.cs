using System.Globalization;
using Platform.Api.Modules.Catalog.Dtos;

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
        IReadOnlyCollection<(Guid Id, string Name)> oldCategories,
        IReadOnlyCollection<(Guid Id, string Name)> oldTags,
        IReadOnlyCollection<(Guid Id, string Name)> newCategories,
        IReadOnlyCollection<(Guid Id, string Name)> newTags) =>
        Compact(
            Set("categories", oldCategories, newCategories),
            Set("tags", oldTags, newTags));

    private static CatalogProductHistoryChange? Set(
        string field,
        IReadOnlyCollection<(Guid Id, string Name)> before,
        IReadOnlyCollection<(Guid Id, string Name)> after) =>
        before.Select(x => x.Id).ToHashSet().SetEquals(after.Select(x => x.Id))
            ? null
            : new CatalogProductHistoryChange(field, Names(before.Select(x => x.Name)), Names(after.Select(x => x.Name)));

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
