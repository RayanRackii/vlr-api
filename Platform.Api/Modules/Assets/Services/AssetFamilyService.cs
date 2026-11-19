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

    Task<AssetFamilySchemaPreviewResponse> PreviewSchemaAsync(
        Guid id,
        AssetFamilySchemaPreviewRequest request,
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

        return families.Select(f => ToDetail(f, includeArchived: false)).ToList();
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

        return families.Select(f => ToDetail(f, includeArchived: false)).ToList();
    }

    public async Task<IReadOnlyList<AssetFamilyDetailResponse>> ListCatalogIncludingInactiveAsync(
        CancellationToken cancellationToken)
    {
        var families = await dbContext.AssetFamilies
            .AsNoTracking()
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Label)
            .ToListAsync(cancellationToken);

        return families.Select(f => ToDetail(f, includeArchived: false)).ToList();
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

        return ToDetail(family, includeArchived: true);
    }

    public async Task<AssetFamilyDetailResponse> CreateAsync(
        CreateAssetFamilyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = NormalizeKey(request.Key);
        var label = NormalizeLabel(request.Label);
        var schemaJson = JsonSerializer.Serialize(
            new AssetFieldSchemaDto(BuildDefinitions(request.Fields, null), 1),
            AssetFamilySchemaEvolution.SchemaJsonOptions);

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

        return ToDetail(family, includeArchived: true);
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
        RejectSeedDeactivation(family.Key, request.IsActive);

        var stored = ParseStoredSchemaOrEmpty(family.FieldSchemaJson);
        if (request.ExpectedSchemaVersion is { } expected && expected != stored.Version)
        {
            throw new InvalidOperationException(
                $"Asset family schema changed since it was loaded (current version {stored.Version}).");
        }

        var requested = BuildDefinitions(request.Fields, stored);
        var usage = await LoadUsageAsync(family.Id, cancellationToken);
        var plan = AssetFamilySchemaEvolution.Plan(stored, requested, usage);
        EnforceFieldLimits(plan.Fields);
        if (!plan.CanSave)
        {
            throw new InvalidOperationException(string.Join(" ", plan.BlockingMessages));
        }

        // Archived fields compare without regard to order, so a client that lists them last
        // does not bump the version; a no-op keeps the stored field list (and order) untouched.
        var changed = AssetFamilySchemaEvolution.Signature(plan.Fields)
            != AssetFamilySchemaEvolution.Signature(stored.Fields);
        var schemaJson = JsonSerializer.Serialize(
            new AssetFieldSchemaDto(changed ? plan.Fields : stored.Fields, changed ? stored.Version + 1 : stored.Version),
            AssetFamilySchemaEvolution.SchemaJsonOptions);

        family.Label = label;
        family.FieldSchemaJson = schemaJson;
        family.SortOrder = request.SortOrder;
        family.IsActive = request.IsActive;
        family.Touch();

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // FieldSchemaJson is a concurrency token: a concurrent save changed the schema after
            // it was loaded here, so this plan may no longer be valid.
            throw new InvalidOperationException(
                "Asset family schema changed since it was loaded. Reload and try again.",
                ex);
        }

        return ToDetail(family, includeArchived: true);
    }

    public async Task<AssetFamilySchemaPreviewResponse> PreviewSchemaAsync(
        Guid id,
        AssetFamilySchemaPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var family = await dbContext.AssetFamilies
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (family is null)
        {
            throw new KeyNotFoundException($"Asset family '{id}' was not found.");
        }

        var stored = ParseStoredSchemaOrEmpty(family.FieldSchemaJson);
        var requested = BuildDefinitions(request.Fields, stored);
        var usage = await LoadUsageAsync(family.Id, cancellationToken);
        var plan = AssetFamilySchemaEvolution.Plan(stored, requested, usage);
        EnforceFieldLimits(plan.Fields);

        return new AssetFamilySchemaPreviewResponse(
            stored.Version,
            plan.AssetCount,
            plan.TenantCount,
            plan.CanSave,
            plan.Impacts);
    }

    // IgnoreQueryFilters is required. A Super-Admin who has entered a tenant still has an
    // ambient TenantId; the global filter would hide assets of other tenants and under-count
    // the impact of a schema change. Cross-tenant counting stays in this file on purpose.
    private async Task<IReadOnlyList<(Guid TenantId, IReadOnlyDictionary<string, JsonElement> Attributes)>> LoadUsageAsync(
        Guid familyId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Assets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.FamilyId == familyId)
            .Select(a => new { a.TenantId, a.Attributes })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => (r.TenantId, (IReadOnlyDictionary<string, JsonElement>)r.Attributes))
            .ToList();
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
        "longtext",
        "number",
        "boolean",
        "select",
        "multiselect",
        "url",
        "date",
    };

    private static readonly Regex KeyPattern = new(
        "^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(250));


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

    private const int MaxFieldsPerFamily = 60;
    private const int MaxTotalFieldsPerFamily = 200;

    // Only active fields count toward the 60 limit; archived ones are bounded by a generous total.
    private static void EnforceFieldLimits(IReadOnlyList<AssetFieldDefinitionDto> fields)
    {
        if (fields.Count(f => !f.Archived) > MaxFieldsPerFamily)
        {
            throw new ArgumentException($"A family can have at most {MaxFieldsPerFamily} active fields.");
        }

        if (fields.Count > MaxTotalFieldsPerFamily)
        {
            throw new ArgumentException(
                $"A family can have at most {MaxTotalFieldsPerFamily} fields, archived included.");
        }
    }

    private static List<AssetFieldDefinitionDto> BuildDefinitions(
        IReadOnlyList<AssetFamilyFieldDto>? fields,
        AssetFieldSchemaDto? stored)
    {
        if (fields is null)
        {
            throw new ArgumentException("Fields are required.");
        }

        if (fields.Count > MaxTotalFieldsPerFamily)
        {
            throw new ArgumentException(
                $"A family can have at most {MaxTotalFieldsPerFamily} fields, archived included.");
        }

        // Keys already present in the stored schema are accepted as is (grandfathered).
        var storedKeys = new HashSet<string>(
            stored?.Fields.Select(f => f.Key) ?? [],
            StringComparer.Ordinal);
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

            if (!storedKeys.Contains(key) && !KeyPattern.IsMatch(key))
            {
                throw new ArgumentException(
                    $"Field key '{key}' must start with a letter and contain only lowercase letters, digits, and underscores (max 64 characters).");
            }

            if (string.IsNullOrWhiteSpace(field.Type))
            {
                throw new ArgumentException($"Field '{key}' type is required.");
            }

            var type = field.Type.Trim().ToLowerInvariant();
            if (!AllowedFieldTypes.Contains(type))
            {
                throw new ArgumentException(
                    $"Field '{key}' type must be one of: text, longtext, number, boolean, select, multiselect, url, date.");
            }

            definitions.Add(BuildDefinition(key, type, field));
        }

        EnforceFieldLimits(definitions);
        return definitions;
    }

    private static AssetFieldDefinitionDto BuildDefinition(string key, string type, AssetFamilyFieldDto field)
    {
        var label = LimitedOrNull(field.Label, 120, key, "label");
        var help = LimitedOrNull(field.Help, 300, key, "help");
        var group = LimitedOrNull(field.Group, 60, key, "group");

        string? unit = null;
        decimal? min = null;
        decimal? max = null;
        int? decimals = null;
        IReadOnlyList<string>? options = null;
        int? maxLength = null;

        switch (type)
        {
            case "number":
                unit = LimitedOrNull(field.Unit, 16, key, "unit");
                min = NormalizeDecimal(field.Min);
                max = NormalizeDecimal(field.Max);
                if (min is { } lo && max is { } hi && lo > hi)
                {
                    throw new ArgumentException($"Field '{key}' min must be less than or equal to max.");
                }

                if (field.Decimals is { } d)
                {
                    if (d is < 0 or > 6)
                    {
                        throw new ArgumentException($"Field '{key}' decimals must be between 0 and 6.");
                    }

                    decimals = d;
                }

                break;

            case "text":
                maxLength = ValidateMaxLength(key, field.MaxLength, 1000);
                break;

            case "longtext":
                maxLength = ValidateMaxLength(key, field.MaxLength, 10000);
                break;

            case "select":
            case "multiselect":
                options = ValidateOptions(key, field.Options);
                break;
        }

        // Archived fields are never required.
        var required = field.Required && !field.Archived;

        return new AssetFieldDefinitionDto(
            key, type, required, label, help, unit, min, max, decimals, options, maxLength, group, field.Archived);
    }

    private static string? LimitedOrNull(string? value, int maxLength, string key, string property)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                $"Field '{key}' {property} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    private static decimal? NormalizeDecimal(decimal? value) =>
        value is { } v ? v / 1.0000000000000000000000000000m : null;

    private static int? ValidateMaxLength(string key, int? value, int ceiling)
    {
        if (value is null)
        {
            return null;
        }

        if (value < 1 || value > ceiling)
        {
            throw new ArgumentException($"Field '{key}' maxLength must be between 1 and {ceiling}.");
        }

        return value;
    }

    private static IReadOnlyList<string> ValidateOptions(string key, IReadOnlyList<string>? options)
    {
        if (options is null || options.Count == 0)
        {
            throw new ArgumentException($"Field '{key}' requires at least one option.");
        }

        if (options.Count > 50)
        {
            throw new ArgumentException($"Field '{key}' can have at most 50 options.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(options.Count);
        foreach (var option in options)
        {
            var trimmed = option?.Trim() ?? string.Empty;
            if (trimmed.Length is < 1 or > 60)
            {
                throw new ArgumentException($"Field '{key}' options must have 1 to 60 characters.");
            }

            if (trimmed.Contains(',', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Field '{key}' options cannot contain commas.");
            }

            if (!seen.Add(trimmed))
            {
                throw new ArgumentException($"Field '{key}' option '{trimmed}' is duplicated.");
            }

            result.Add(trimmed);
        }

        return result;
    }

    private static AssetFieldSchemaDto ParseStoredSchemaOrEmpty(string fieldSchemaJson)
    {
        try
        {
            return AssetFamilyAttributeValidator.ParseSchema(fieldSchemaJson);
        }
        catch (ArgumentException)
        {
            // A broken stored schema must not prevent an admin from replacing it.
            return new AssetFieldSchemaDto([]);
        }
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

    private static AssetFamilyDetailResponse ToDetail(
        Platform.Core.Domain.Entities.AssetFamily family,
        bool includeArchived)
    {
        var schema = AssetFamilyAttributeValidator.ParseSchema(family.FieldSchemaJson);
        var fields = schema.Fields
            .Where(f => includeArchived || !f.Archived)
            .Select(f => new AssetFamilyFieldDto(
                f.Key, f.Type, f.Required, f.Label, f.Help, f.Unit, f.Min, f.Max,
                f.Decimals, f.Options, f.MaxLength, f.Group, f.Archived))
            .ToList();

        return new AssetFamilyDetailResponse(
            family.Id,
            family.Key,
            family.Label,
            fields,
            family.SortOrder,
            family.IsActive,
            schema.Version);
    }
}
