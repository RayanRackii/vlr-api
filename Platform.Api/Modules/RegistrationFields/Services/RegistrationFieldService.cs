using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.RegistrationFields.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.RegistrationFields.Services;

public sealed class RegistrationFieldService(AppDbContext dbContext) : IRegistrationFieldService
{
    private static readonly string[] CoreFields =
        ["name", "email", "password", "phone", "customerType", "document"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<RegistrationSchemaResponseDto> GetSchemaBySubdomainAsync(
        string subdomain,
        CancellationToken cancellationToken)
    {
        var normalized = subdomain.Trim().ToLowerInvariant();
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.Subdomain == normalized && t.IsActive,
                cancellationToken)
            ?? throw new KeyNotFoundException("Tenant not found.");

        var fields = await dbContext.TenantRegistrationFields
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenant.Id)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.FieldKey)
            .ToListAsync(cancellationToken);

        return new RegistrationSchemaResponseDto(
            CoreFields,
            fields.Select(ToDto).ToList());
    }

    public async Task<IReadOnlyList<RegistrationFieldDto>> ListForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await EnsureTenantExistsAsync(tenantId, cancellationToken);

        var fields = await dbContext.TenantRegistrationFields
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(f => f.TenantId == tenantId)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.FieldKey)
            .ToListAsync(cancellationToken);

        return fields.Select(ToDto).ToList();
    }

    public async Task<RegistrationFieldDto> CreateAsync(
        Guid tenantId,
        UpsertRegistrationFieldRequestDto request,
        CancellationToken cancellationToken)
    {
        await EnsureTenantExistsAsync(tenantId, cancellationToken);

        if (!RegistrationFieldTypes.TryNormalize(request.FieldType, out var fieldType))
        {
            throw new ArgumentException($"Unknown field type '{request.FieldType}'.");
        }

        var key = request.FieldKey.Trim();
        if (RegistrationFieldTypes.ReservedKeys.Contains(key))
        {
            throw new ArgumentException(
                $"FieldKey '{key}' is reserved for core registration fields.");
        }

        var optionsJson = SerializeOptions(fieldType, request.Options);

        var exists = await dbContext.TenantRegistrationFields
            .IgnoreQueryFilters()
            .AnyAsync(f => f.TenantId == tenantId && f.FieldKey == key, cancellationToken);

        if (exists)
        {
            throw new InvalidOperationException(
                $"A field with key '{key}' already exists for this tenant.");
        }

        var entity = new TenantRegistrationField(
            tenantId,
            key,
            request.Label,
            fieldType,
            request.IsRequired,
            request.SortOrder,
            optionsJson);

        dbContext.TenantRegistrationFields.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(entity);
    }

    public async Task<RegistrationFieldDto> UpdateAsync(
        Guid tenantId,
        Guid fieldId,
        UpdateRegistrationFieldRequestDto request,
        CancellationToken cancellationToken)
    {
        if (!RegistrationFieldTypes.TryNormalize(request.FieldType, out var fieldType))
        {
            throw new ArgumentException($"Unknown field type '{request.FieldType}'.");
        }

        var entity = await dbContext.TenantRegistrationFields
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fieldId && f.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Registration field not found.");

        var optionsJson = SerializeOptions(fieldType, request.Options);
        entity.Update(request.Label, fieldType, request.IsRequired, request.SortOrder, optionsJson);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToDto(entity);
    }

    public async Task DeleteAsync(
        Guid tenantId,
        Guid fieldId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.TenantRegistrationFields
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == fieldId && f.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Registration field not found.");

        dbContext.TenantRegistrationFields.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureTenantExistsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Tenants
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Id == tenantId, cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException("Tenant not found.");
        }
    }

    private static string? SerializeOptions(string fieldType, IReadOnlyList<string>? options)
    {
        if (fieldType != RegistrationFieldTypes.Select)
        {
            return null;
        }

        if (options is null || options.Count == 0)
        {
            throw new ArgumentException("Select fields require at least one option.");
        }

        return JsonSerializer.Serialize(
            options.Select(o => o.Trim()).Where(o => o.Length > 0).ToList(),
            JsonOptions);
    }

    private static RegistrationFieldDto ToDto(TenantRegistrationField field)
    {
        IReadOnlyList<string>? options = null;
        if (!string.IsNullOrWhiteSpace(field.OptionsJson))
        {
            options = JsonSerializer.Deserialize<List<string>>(field.OptionsJson, JsonOptions);
        }

        return new RegistrationFieldDto(
            field.Id,
            field.FieldKey,
            field.Label,
            field.FieldType,
            field.IsRequired,
            field.SortOrder,
            options);
    }
}
