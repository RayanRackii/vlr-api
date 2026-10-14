using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Services;

public interface IAssetFamilyService
{
    Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogIncludingInactiveAsync(
        CancellationToken cancellationToken);

    Task<AssetFamilyDetailResponse> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken);

    Task<AssetFamilyDetailResponse> CreateAsync(
        CreateAssetFamilyRequest request,
        CancellationToken cancellationToken);

    Task<AssetFamilyDetailResponse> UpdateAsync(
        Guid id,
        UpdateAssetFamilyRequest request,
        CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class AssetFamilyService(AppDbContext dbContext) : IAssetFamilyService
{
    public async Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogAsync(
        CancellationToken cancellationToken)
    {
        var families = await dbContext.AssetFamilies
            .AsNoTracking()
            .Where(f => f.IsActive)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Label)
            .ToListAsync(cancellationToken);

        return families.Select(ToDetail).ToList();
    }

    public async Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var families = await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Join(
                dbContext.AssetFamilies.AsNoTracking().Where(f => f.IsActive),
                t => t.FamilyId,
                f => f.Id,
                (_, f) => f)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Label)
            .ToListAsync(cancellationToken);

        return families.Select(ToDetail).ToList();
    }

    public async Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogIncludingInactiveAsync(
        CancellationToken cancellationToken)
    {
        var families = await dbContext.AssetFamilies
            .AsNoTracking()
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Label)
            .ToListAsync(cancellationToken);

        return families.Select(ToDetail).ToList();
    }

    public async Task<AssetFamilyDetailResponse> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var family = await dbContext.AssetFamilies
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (family is null)
        {
            throw new KeyNotFoundException($"Asset family '{id}' was not found.");
        }

        return ToDetail(family);
    }

    public async Task<AssetFamilyDetailResponse> CreateAsync(
        CreateAssetFamilyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = NormalizeKey(request.Key);
        var label = NormalizeLabel(request.Label);
        var schemaJson = SerializeSchema(request.Fields);

        var duplicate = await dbContext.AssetFamilies
            .AnyAsync(f => f.Key == key, cancellationToken);

        if (duplicate)
        {
            throw new InvalidOperationException($"Asset family key '{key}' already exists.");
        }

        RejectSeedDeactivation(key, request.IsActive);

        var family = new AssetFamily
        {
            Key = key,
            Label = label,
            FieldSchemaJson = schemaJson,
            SortOrder = request.SortOrder,
            IsActive = request.IsActive,
        };

        dbContext.AssetFamilies.Add(family);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsAssetFamilyKeyUniqueViolation(ex))
        {
            throw new InvalidOperationException($"Asset family key '{key}' already exists.", ex);
        }

        return ToDetail(family);
    }

    public async Task<AssetFamilyDetailResponse> UpdateAsync(
        Guid id,
        UpdateAssetFamilyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var family = await dbContext.AssetFamilies
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (family is null)
        {
            throw new KeyNotFoundException($"Asset family '{id}' was not found.");
        }

        RejectKeyChange(family.Key, request.Key);

        var label = NormalizeLabel(request.Label);
        var schemaJson = SerializeSchema(request.Fields);
        RejectSeedDeactivation(family.Key, request.IsActive);

        family.Label = label;
        family.FieldSchemaJson = schemaJson;
        family.SortOrder = request.SortOrder;
        family.IsActive = request.IsActive;
        family.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDetail(family);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var family = await dbContext.AssetFamilies
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (family is null)
        {
            throw new KeyNotFoundException($"Asset family '{id}' was not found.");
        }

        if (SeedLockedKeys.Contains(family.Key))
        {
            throw new InvalidOperationException("Seed asset families cannot be deleted.");
        }

        // IgnoreQueryFilters is required. A Super-Admin who has entered a tenant still
        // has an ambient TenantId; the global filter would hide assets.assets and
        // tenant_asset_families rows in other tenants and under-count references.
        var assetReferences = await dbContext.Assets
            .IgnoreQueryFilters()
            .CountAsync(asset => asset.FamilyId == family.Id, cancellationToken);

        var tenantReferences = await dbContext.TenantAssetFamilies
            .IgnoreQueryFilters()
            .CountAsync(link => link.FamilyId == family.Id, cancellationToken);

        if (assetReferences > 0 || tenantReferences > 0)
        {
            throw new InvalidOperationException("Asset family is in use and cannot be deleted.");
        }

        dbContext.AssetFamilies.Remove(family);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsAssetFamilyRestrictViolation(ex))
        {
            throw new InvalidOperationException(
                "Asset family is in use and cannot be deleted.",
                ex);
        }
    }

    private static readonly HashSet<string> SeedLockedKeys = new(StringComparer.Ordinal)
    {
        AssetFamilyKeys.Spaces,
        AssetFamilyKeys.Electrical,
        AssetFamilyKeys.Goods,
        AssetFamilyKeys.Generic,
    };

    private static readonly HashSet<string> AllowedFieldTypes = new(StringComparer.Ordinal)
    {
        "text",
        "number",
        "boolean",
    };

    private static readonly Regex KeyPattern = new(
        "^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));

    private static readonly JsonSerializerOptions SchemaJsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };

    private static void RejectKeyChange(string currentKey, string? requestedKey)
    {
        if (string.IsNullOrWhiteSpace(requestedKey))
        {
            return;
        }

        var normalized = NormalizeKey(requestedKey);
        if (!string.Equals(normalized, currentKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("Key cannot be changed.");
        }
    }

    private static void RejectSeedDeactivation(string key, bool isActive)
    {
        if (!isActive && SeedLockedKeys.Contains(key))
        {
            throw new InvalidOperationException("Seed asset families cannot be deactivated.");
        }
    }

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Key is required.");
        }

        var normalized = key.Trim().ToLowerInvariant();
        if (!KeyPattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                "Key must start with a letter and contain only lowercase letters, digits, and underscores (max 64 characters).");
        }

        return normalized;
    }

    private static string NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Label is required.");
        }

        var trimmed = label.Trim();
        if (trimmed.Length > 200)
        {
            throw new ArgumentException("Label must be at most 200 characters.");
        }

        return trimmed;
    }

    private static string SerializeSchema(IReadOnlyList<AssetFamilyFieldDto>? fields)
    {
        if (fields is null)
        {
            throw new ArgumentException("Fields are required.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<AssetFieldDefinitionDto>(fields.Count);

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
            {
                throw new ArgumentException("Field key is required.");
            }

            var key = field.Key.Trim();
            if (!seen.Add(key))
            {
                throw new ArgumentException($"Field key '{key}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(field.Type))
            {
                throw new ArgumentException($"Field '{key}' type is required.");
            }

            var type = field.Type.Trim().ToLowerInvariant();
            if (!AllowedFieldTypes.Contains(type))
            {
                throw new ArgumentException(
                    $"Field '{key}' type must be text, number, or boolean.");
            }

            var label = string.IsNullOrWhiteSpace(field.Label) ? null : field.Label.Trim();
            definitions.Add(new AssetFieldDefinitionDto(key, type, field.Required, label));
        }

        return JsonSerializer.Serialize(new AssetFieldSchemaDto(definitions), SchemaJsonOptions);
    }

    private static bool IsAssetFamilyKeyUniqueViolation(DbUpdateException exception)
    {
        for (var current = exception as Exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState == PostgresErrorCodes.UniqueViolation
                && postgres.ConstraintName is not null
                && postgres.ConstraintName.Contains("key", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssetFamilyRestrictViolation(DbUpdateException exception)
    {
        for (var current = exception as Exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres
                && postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                return true;
            }
        }

        return false;
    }

    private static AssetFamilyDetailResponse ToDetail(Platform.Core.Domain.Entities.AssetFamily family)
    {
        var schema = AssetFamilyAttributeValidator.ParseSchema(family.FieldSchemaJson);
        var fields = schema.Fields
            .Select(f => new AssetFamilyFieldDto(f.Key, f.Type, f.Required, f.Label))
            .ToList();

        return new AssetFamilyDetailResponse(
            family.Id,
            family.Key,
            family.Label,
            fields,
            family.SortOrder,
            family.IsActive);
    }
}
