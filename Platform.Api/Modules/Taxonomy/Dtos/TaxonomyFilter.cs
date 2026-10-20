using Platform.Api.Modules.Taxonomy.Services;

namespace Platform.Api.Modules.Taxonomy.Dtos;

public sealed record TaxonomyFilter(Guid? CategoryId, IReadOnlyList<Guid> TagIds)
{
    public static TaxonomyFilter Parse(string? category, string? tags)
    {
        Guid? categoryId = null;
        if (!string.IsNullOrEmpty(category))
        {
            if (!Guid.TryParse(category, out var id)) throw new TaxonomyException("Invalid category filter.");
            categoryId = id;
        }
        var ids = new HashSet<Guid>();
        if (!string.IsNullOrEmpty(tags))
        {
            foreach (var part in tags.Split(','))
            {
                if (!Guid.TryParse(part, out var id)) throw new TaxonomyException("Invalid tag filter.");
                ids.Add(id);
            }
        }
        if (ids.Count > 20) throw new TaxonomyException("At most 20 distinct tags are allowed.");
        return new(categoryId, ids.ToArray());
    }
}
