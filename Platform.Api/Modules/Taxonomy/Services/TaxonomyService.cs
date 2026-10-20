using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Taxonomy.Services;

public sealed class TaxonomyException(string message, string code = "TAXONOMY_INVALID", int status = 400)
    : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed class TaxonomyService(AppDbContext db, ITenantProvider tenantProvider)
{
    public Guid RequireTenant() => tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(CancellationToken ct)
    {
        var tenant = RequireTenant();
        return (await db.TaxonomyCategories.AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<IReadOnlyList<TagDto>> TagsAsync(CancellationToken ct)
    {
        var tenant = RequireTenant();
        return (await db.TaxonomyTags.AsNoTracking().Where(x => x.TenantId == tenant)
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct)).Select(Map).ToList();
    }

    public async Task<CategoryDto?> CategoryAsync(Guid id, CancellationToken ct)
    {
        var tenant = RequireTenant();
        var entity = await db.TaxonomyCategories.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<TagDto?> TagAsync(Guid id, CancellationToken ct)
    {
        var tenant = RequireTenant();
        var entity = await db.TaxonomyTags.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<CategoryDto> CreateCategoryAsync(CreateCategoryRequest request, CancellationToken ct)
    {
        var tenant = RequireTenant();
        await using var scope = await TaxonomyMutationScope.BeginAsync(db, tenant, ct);
        var name = Name(request.Name, 100);
        ValidateSlug(request.Slug);
        ValidatePresentation(request.Icon, request.SortOrder);
        await EnsureCategorySlugAsync(tenant, request.Slug, ct);
        var entity = new TaxonomyCategory { TenantId = tenant, Name = name, Slug = request.Slug,
            ParentId = request.ParentId, Icon = request.Icon, SortOrder = request.SortOrder };
        var tree = await db.TaxonomyCategories.AsNoTracking().Where(x => x.TenantId == tenant).ToListAsync(ct);
        tree.Add(entity);
        ValidateHierarchy(tree);
        db.TaxonomyCategories.Add(entity);
        await SaveAsync(ct);
        await scope.CommitAsync(ct);
        return Map(entity);
    }

    public async Task<CategoryDto?> UpdateCategoryAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct)
    {
        var tenant = RequireTenant();
        await using var scope = await TaxonomyMutationScope.BeginAsync(db, tenant, ct);
        var entity = await db.TaxonomyCategories.FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct);
        if (entity is null) return null;
        RejectSlugChange(request.Extra, entity.Slug);
        var name = Name(request.Name, 100);
        ValidatePresentation(request.Icon, request.SortOrder);
        var tree = await db.TaxonomyCategories.AsNoTracking().Where(x => x.TenantId == tenant).ToListAsync(ct);
        tree.Single(x => x.Id == id).ParentId = request.ParentId;
        ValidateHierarchy(tree);
        entity.Name = name;
        entity.ParentId = request.ParentId;
        entity.Icon = request.Icon;
        entity.SortOrder = request.SortOrder;
        entity.Touch();
        await SaveAsync(ct);
        await scope.CommitAsync(ct);
        return Map(entity);
    }

    public async Task<TagDto> CreateTagAsync(CreateTagRequest request, CancellationToken ct)
    {
        var tenant = RequireTenant();
        await using var scope = await TaxonomyMutationScope.BeginAsync(db, tenant, ct);
        var name = Name(request.Name, 50);
        ValidateSlug(request.Slug);
        var normalized = NormalizeTagName(name);
        await EnsureTagAvailableAsync(tenant, normalized, request.Slug, null, ct);
        var entity = new TaxonomyTag { TenantId = tenant, Name = name, NormalizedName = normalized, Slug = request.Slug };
        db.TaxonomyTags.Add(entity);
        await SaveAsync(ct);
        await scope.CommitAsync(ct);
        return Map(entity);
    }

    public async Task<TagDto?> UpdateTagAsync(Guid id, UpdateTagRequest request, CancellationToken ct)
    {
        var tenant = RequireTenant();
        await using var scope = await TaxonomyMutationScope.BeginAsync(db, tenant, ct);
        var entity = await db.TaxonomyTags.FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct);
        if (entity is null) return null;
        RejectSlugChange(request.Extra, entity.Slug);
        var name = Name(request.Name, 50);
        var normalized = NormalizeTagName(name);
        await EnsureTagAvailableAsync(tenant, normalized, entity.Slug, id, ct);
        entity.Name = name;
        entity.NormalizedName = normalized;
        entity.Touch();
        await SaveAsync(ct);
        await scope.CommitAsync(ct);
        return Map(entity);
    }

    public async Task<bool> DeleteCategoryAsync(Guid id, CancellationToken ct)
    {
        var tenant = RequireTenant();
        await using var scope = await TaxonomyMutationScope.BeginAsync(db, tenant, ct);
        var entity = await db.TaxonomyCategories.FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct);
        if (entity is null) return false;
        if (await db.TaxonomyCategories.AnyAsync(x => x.TenantId == tenant && x.ParentId == id, ct)
            || await db.CatalogProductCategories.AnyAsync(x => x.TenantId == tenant && x.CategoryId == id, ct))
            throw InUse();
        db.TaxonomyCategories.Remove(entity);
        await SaveAsync(ct);
        await scope.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeleteTagAsync(Guid id, CancellationToken ct)
    {
        var tenant = RequireTenant();
        await using var scope = await TaxonomyMutationScope.BeginAsync(db, tenant, ct);
        var entity = await db.TaxonomyTags.FirstOrDefaultAsync(x => x.TenantId == tenant && x.Id == id, ct);
        if (entity is null) return false;
        if (await db.CatalogProductTags.AnyAsync(x => x.TenantId == tenant && x.TagId == id, ct)) throw InUse();
        db.TaxonomyTags.Remove(entity);
        await SaveAsync(ct);
        await scope.CommitAsync(ct);
        return true;
    }

    public static string NormalizeTagName(string name) => Regex.Replace(name.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ").ToLowerInvariant();
    public static CategoryDto Map(TaxonomyCategory x) => new(x.Id, x.Name, x.Slug, x.ParentId, x.Icon, x.SortOrder);
    public static TagDto Map(TaxonomyTag x) => new(x.Id, x.Name, x.Slug);

    public static void ValidateHierarchy(IReadOnlyList<TaxonomyCategory> tree)
    {
        var parents = tree.ToDictionary(x => x.Id, x => x.ParentId);
        foreach (var category in tree)
        {
            var visited = new HashSet<Guid>();
            Guid? current = category.Id;
            while (current is { } id)
            {
                if (!parents.TryGetValue(id, out var parent)) throw new TaxonomyException("Invalid category parent.");
                if (!visited.Add(id)) throw new TaxonomyException("Category hierarchy cannot contain cycles.");
                if (visited.Count > 3) throw new TaxonomyException("Categories support at most three levels.");
                current = parent;
            }
        }
    }

    public static async Task SaveAsync(AppDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && IsTaxonomyConstraint(pg.ConstraintName))
        {
            if (pg.SqlState == PostgresErrorCodes.UniqueViolation) throw Duplicate();
            if (pg.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                if (ex.Entries.Any(e => e.State == EntityState.Deleted)) throw InUse();
                throw new TaxonomyException("Invalid taxonomy reference.");
            }
            throw;
        }
    }

    private Task SaveAsync(CancellationToken ct) => SaveAsync(db, ct);
    private static bool IsTaxonomyConstraint(string? name) => name is not null
        && (name.Contains("taxonomy", StringComparison.Ordinal) || name.Contains("catalog_product_categories", StringComparison.Ordinal)
            || name.Contains("catalog_product_tags", StringComparison.Ordinal));
    private static string Name(string? name, int max)
    {
        var value = name?.Trim() ?? "";
        if (value.Length < 1 || value.Length > max) throw new TaxonomyException($"Name must contain 1–{max} characters.");
        return value;
    }
    private static void ValidateSlug(string? slug)
    {
        if (slug is null || slug.Length is < 1 or > 80 || !Regex.IsMatch(slug, @"\A[a-z0-9]+(-[a-z0-9]+)*\z"))
            throw new TaxonomyException("Invalid slug.");
    }
    private static void ValidatePresentation(string? icon, int sortOrder)
    {
        if (sortOrder is < 0 or > 100000 || (icon is not null && !new[] { "folder", "tag", "package", "map-pin", "calendar", "star" }.Contains(icon)))
            throw new TaxonomyException("Invalid icon or sort order.");
    }
    private static void RejectSlugChange(Dictionary<string, JsonElement>? extra, string slug)
    {
        if (extra is null) return;
        foreach (var (key, value) in extra)
            if (key.Equals("slug", StringComparison.OrdinalIgnoreCase)
                && (value.ValueKind != JsonValueKind.String || value.GetString() != slug))
                throw new TaxonomyException("Slugs are immutable.");
    }
    private async Task EnsureCategorySlugAsync(Guid tenant, string slug, CancellationToken ct)
    {
        if (await db.TaxonomyCategories.AnyAsync(x => x.TenantId == tenant && x.Slug == slug, ct)) throw Duplicate();
    }
    private async Task EnsureTagAvailableAsync(Guid tenant, string normalized, string slug, Guid? except, CancellationToken ct)
    {
        if (await db.TaxonomyTags.AnyAsync(x => x.TenantId == tenant && x.Id != except && (x.NormalizedName == normalized || x.Slug == slug), ct)) throw Duplicate();
    }
    private static TaxonomyException Duplicate() => new("Taxonomy label already exists.", "TAXONOMY_DUPLICATE", 409);
    private static TaxonomyException InUse() => new("Taxonomy label is in use.", "TAXONOMY_IN_USE", 409);
}
