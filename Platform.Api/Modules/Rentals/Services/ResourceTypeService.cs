using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public interface IResourceTypeService
{
    Task<IReadOnlyList<ResourceTypeResponse>> ListAsync(CancellationToken cancellationToken);

    Task<ResourceTypeResponse> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<ResourceTypeResponse> CreateAsync(ResourceTypeWriteRequest request, CancellationToken cancellationToken);

    Task<ResourceTypeResponse> UpdateAsync(Guid id, ResourceTypeWriteRequest request, CancellationToken cancellationToken);

    Task EnsureAsync(Guid tenantId, Guid typeId, CancellationToken cancellationToken);
}

public sealed class ResourceTypeService(AppDbContext dbContext, ITenantProvider tenantProvider) : IResourceTypeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ResourceTypeResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await EnsureSeedsAsync(tenantId, cancellationToken);
        var types = await dbContext.ResourceTypes.AsNoTracking()
            .Where(type => type.TenantId == tenantId)
            .OrderByDescending(type => type.Seeded)
            .ThenBy(type => type.Name)
            .ToListAsync(cancellationToken);
        var counts = await CountsAsync(tenantId, cancellationToken);
        return types.Select(type => ToResponse(type, counts.GetValueOrDefault(type.Id))).ToList();
    }

    public async Task<ResourceTypeResponse> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await EnsureSeedsAsync(tenantId, cancellationToken);
        var type = await FindAsync(tenantId, id, tracking: false, cancellationToken)
            ?? throw new KeyNotFoundException("Resource type was not found.");
        var counts = await CountsAsync(tenantId, cancellationToken);
        return ToResponse(type, counts.GetValueOrDefault(type.Id));
    }

    public async Task<ResourceTypeResponse> CreateAsync(ResourceTypeWriteRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await EnsureSeedsAsync(tenantId, cancellationToken);
        var draft = Normalize(request, currentKey: null, seeded: false);
        if (await dbContext.ResourceTypes.AnyAsync(type => type.TenantId == tenantId && type.Key == draft.Key, cancellationToken))
        {
            throw new InvalidOperationException($"Resource type key '{draft.Key}' already exists.");
        }

        var type = new ResourceType
        {
            TenantId = tenantId,
            Key = draft.Key,
            Name = draft.Name,
            Singular = draft.Singular,
            Plural = draft.Plural,
            Article = draft.Article,
            Seeded = false,
            Bookable = draft.Bookable,
            Deliverable = draft.Deliverable,
            Maintenance = draft.Maintenance,
            FieldsJson = draft.FieldsJson,
        };
        dbContext.ResourceTypes.Add(type);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueKey(ex))
        {
            throw new InvalidOperationException($"Resource type key '{draft.Key}' already exists.", ex);
        }

        return ToResponse(type, 0);
    }

    public async Task<ResourceTypeResponse> UpdateAsync(Guid id, ResourceTypeWriteRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await EnsureSeedsAsync(tenantId, cancellationToken);
        var type = await FindAsync(tenantId, id, tracking: true, cancellationToken)
            ?? throw new KeyNotFoundException("Resource type was not found.");
        var draft = Normalize(request, type.Key, type.Seeded);
        if (draft.Key != type.Key
            && await dbContext.ResourceTypes.AnyAsync(
                other => other.TenantId == tenantId && other.Key == draft.Key && other.Id != type.Id,
                cancellationToken))
        {
            throw new InvalidOperationException($"Resource type key '{draft.Key}' already exists.");
        }

        type.Key = draft.Key;
        type.Name = draft.Name;
        type.Singular = draft.Singular;
        type.Plural = draft.Plural;
        type.Article = draft.Article;
        type.Bookable = draft.Bookable;
        type.Deliverable = draft.Deliverable;
        type.Maintenance = draft.Maintenance;
        type.FieldsJson = draft.FieldsJson;
        type.Touch();
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueKey(ex))
        {
            throw new InvalidOperationException($"Resource type key '{draft.Key}' already exists.", ex);
        }
        var counts = await CountsAsync(tenantId, cancellationToken);
        return ToResponse(type, counts.GetValueOrDefault(type.Id));
    }

    public async Task EnsureAsync(Guid tenantId, Guid typeId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.ResourceTypes.AsNoTracking()
            .AnyAsync(type => type.TenantId == tenantId && type.Id == typeId, cancellationToken);
        if (!exists)
        {
            throw new ArgumentException("Resource type was not found for this tenant.");
        }
    }

    internal static async Task EnsureSeedsAsync(AppDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        var keys = await db.ResourceTypes.AsNoTracking()
            .Where(type => type.TenantId == tenantId && (type.Key == "quadra" || type.Key == "cacamba"))
            .Select(type => type.Key)
            .ToListAsync(cancellationToken);
        var present = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var seed in Seeds.Where(seed => !present.Contains(seed.Key)))
        {
            db.ResourceTypes.Add(new ResourceType
            {
                TenantId = tenantId,
                Key = seed.Key,
                Name = seed.Name,
                Singular = seed.Singular,
                Plural = seed.Plural,
                Article = seed.Article,
                Seeded = true,
                Bookable = seed.Bookable,
                Deliverable = seed.Deliverable,
                Maintenance = seed.Maintenance,
                FieldsJson = "[]",
            });
        }

        if (db.ChangeTracker.HasChanges())
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueKey(ex))
            {
                foreach (var entry in db.ChangeTracker.Entries<ResourceType>().Where(item => item.State == EntityState.Added).ToList())
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }

    private static bool IsUniqueKey(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation;

    private async Task EnsureSeedsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await EnsureSeedsAsync(dbContext, tenantId, cancellationToken);

    private async Task<Dictionary<Guid, int>> CountsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.RentalAssets.AsNoTracking()
            .Where(asset => asset.TenantId == tenantId && asset.ResourceTypeId != null)
            .GroupBy(asset => asset.ResourceTypeId!.Value)
            .Select(group => new { Id = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.Id, row => row.Count);
    }

    private Task<ResourceType?> FindAsync(Guid tenantId, Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? dbContext.ResourceTypes : dbContext.ResourceTypes.AsNoTracking();
        return query.FirstOrDefaultAsync(type => type.TenantId == tenantId && type.Id == id, cancellationToken);
    }

    private static ResourceTypeResponse ToResponse(ResourceType type, int count) =>
        new(
            type.Id,
            type.Key,
            type.Name,
            type.Singular,
            type.Plural,
            type.Article,
            type.Seeded,
            count,
            new ResourceTypeCapabilitiesDto(type.Bookable, type.Deliverable, type.Maintenance),
            ReadFields(type.FieldsJson));

    private static IReadOnlyList<ResourceTypeFieldDto> ReadFields(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<ResourceTypeFieldDto>>(json, JsonOptions) ?? [];
    }

    private static Draft Normalize(ResourceTypeWriteRequest request, string? currentKey, bool seeded)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Capabilities);
        var name = Required(request.Name, "Name", 80);
        var key = string.IsNullOrWhiteSpace(request.Key)
            ? currentKey ?? Slug(name)
            : Slug(request.Key);
        if (key.Length is < 1 or > 40)
        {
            throw new ArgumentException("Key must be between 1 and 40 characters.");
        }

        if (seeded && currentKey is not null && !string.Equals(key, currentKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("Seeded resource types keep their key.");
        }

        var article = request.Article.Trim().ToLowerInvariant();
        if (article is not ("um" or "uma"))
        {
            throw new ArgumentException("Article must be um or uma.");
        }

        var fields = NormalizeFields(request.Fields ?? []);
        return new Draft(
            key,
            name,
            Required(request.Singular, "Singular", 80),
            Required(request.Plural, "Plural", 80),
            article,
            request.Capabilities.Bookable,
            request.Capabilities.Deliverable,
            request.Capabilities.Maintenance,
            JsonSerializer.Serialize(fields, JsonOptions));
    }

    private static List<ResourceTypeFieldDto> NormalizeFields(IReadOnlyList<ResourceTypeFieldDto> fields)
    {
        if (fields.Count > 40)
        {
            throw new ArgumentException("A resource type can have at most 40 fields.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<ResourceTypeFieldDto>(fields.Count);
        foreach (var field in fields)
        {
            var key = Slug(field.Key);
            if (key.Length is < 1 or > 40 || !keys.Add(key))
            {
                throw new ArgumentException("Each field needs a unique key of 1 to 40 characters.");
            }

            var responseType = CanonicalType(field.ResponseType ?? field.Type);
            var visibility = field.Visibility?.Trim().ToLowerInvariant() switch
            {
                "internal" => "internal",
                null or "" or "public" => "public",
                _ => throw new ArgumentException("Field visibility must be public or internal."),
            };
            var name = string.IsNullOrWhiteSpace(field.Name) ? field.Label : field.Name;
            name = string.IsNullOrWhiteSpace(name) ? key : name.Trim();
            if (name.Length > 80)
            {
                throw new ArgumentException("Field name must be at most 80 characters.");
            }

            var options = (field.Options ?? []).Select(option => option.Trim()).Where(option => option.Length > 0).ToList();
            if (options.Count > 50 || options.Any(option => option.Length > 80))
            {
                throw new ArgumentException("A field can have at most 50 options of 80 characters.");
            }

            var published = visibility == "public";
            normalized.Add(new ResourceTypeFieldDto
            {
                Key = key,
                Type = responseType,
                Name = name,
                Label = name,
                Required = field.Required,
                Visibility = visibility,
                Filterable = published && field.Filterable,
                AskOnBooking = published && field.AskOnBooking,
                Options = options,
                ResponseType = responseType,
            });
        }

        return normalized;
    }

    private static string CanonicalType(string? value)
    {
        var folded = Fold(value ?? "");
        return folded switch
        {
            "number" or "numero" => "number",
            "list" or "lista" => "list",
            "" or "text" or "texto" => "text",
            _ => throw new ArgumentException("Field responseType must be text, list, or number."),
        };
    }

    private static string Required(string? value, string name, int max)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length < 1 || trimmed.Length > max)
        {
            throw new ArgumentException($"{name} must be between 1 and {max} characters.");
        }

        return trimmed;
    }

    private static string Slug(string? value)
    {
        var folded = Fold(value ?? "");
        var builder = new StringBuilder(folded.Length);
        var dash = false;
        foreach (var character in folded)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                dash = false;
            }
            else if (!dash && builder.Length > 0)
            {
                builder.Append('-');
                dash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string Fold(string value) =>
        value.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Aggregate(new StringBuilder(), (builder, character) => builder.Append(character))
            .ToString()
            .Normalize(NormalizationForm.FormC)
            .Trim()
            .ToLowerInvariant();

    private Guid RequireTenant() =>
        tenantProvider.TenantId is { } tenantId && tenantId != Guid.Empty
            ? tenantId
            : throw new UnauthorizedAccessException("Tenant context is required.");

    private sealed record Draft(
        string Key,
        string Name,
        string Singular,
        string Plural,
        string Article,
        bool Bookable,
        bool Deliverable,
        bool Maintenance,
        string FieldsJson);

    private sealed record Seed(string Key, string Name, string Singular, string Plural, string Article, bool Bookable, bool Deliverable, bool Maintenance);

    private static readonly Seed[] Seeds =
    [
        new("quadra", "Quadra", "quadra", "quadras", "uma", true, false, true),
        new("cacamba", "Caçamba", "caçamba", "caçambas", "uma", false, true, true),
    ];
}
