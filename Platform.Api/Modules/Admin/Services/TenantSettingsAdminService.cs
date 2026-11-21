using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Admin.Services;

public interface ITenantSettingsAdminService
{
    Task<TenantSettingDto> GetAsync(Guid tenantId, string key, CancellationToken cancellationToken);

    Task<TenantSettingDto> PutAsync(
        Guid tenantId, string key, string value, CancellationToken cancellationToken);
}

/// <summary>
/// Platform-admin read/write of a small allow-list of <c>core.tenant_settings</c> keys
/// (<see cref="TenantSettingKeys.AdminEditableKeys"/>). Every key has its own validator; a key that is
/// not on the list is "not found", so this endpoint cannot be used to write arbitrary settings.
/// </summary>
public sealed class TenantSettingsAdminService(AppDbContext dbContext) : ITenantSettingsAdminService
{
    public const int MaxRentalAssetIds = 200;

    public async Task<TenantSettingDto> GetAsync(
        Guid tenantId, string key, CancellationToken cancellationToken)
    {
        EnsureEditableKey(key);
        await EnsureTenantAsync(tenantId, cancellationToken);
        var value = await TenantSettingStore.ReadValueAsync(dbContext, tenantId, key, cancellationToken);
        return new TenantSettingDto(key, value);
    }

    public async Task<TenantSettingDto> PutAsync(
        Guid tenantId, string key, string value, CancellationToken cancellationToken)
    {
        EnsureEditableKey(key);
        await EnsureTenantAsync(tenantId, cancellationToken);
        var canonical = await ValidateAsync(tenantId, key, value, cancellationToken);

        var row = await TenantSettingStore.Query(dbContext, tenantId, key)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            dbContext.TenantSettings.Add(new TenantSetting
            {
                TenantId = tenantId,
                Key = key,
                Value = canonical,
            });
        }
        else
        {
            row.Value = canonical;
            row.Touch();
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new InvalidOperationException("The setting was changed concurrently. Try again.", ex);
        }

        return new TenantSettingDto(key, canonical);
    }

    private async Task<string> ValidateAsync(
        Guid tenantId, string key, string value, CancellationToken cancellationToken)
    {
        if (key == TenantSettingKeys.BookingUiEnabled)
        {
            if (!TenantSettingValues.TryParseBoolean(value, out var enabled))
            {
                throw new ArgumentException("Value must be \"true\" or \"false\".");
            }

            return enabled ? "true" : "false";
        }

        if (key == TenantSettingKeys.TeacherLessonRentalAssetIds)
        {
            if (!TenantSettingValues.TryParseRentalAssetIds(value, out var ids))
            {
                throw new ArgumentException("Value must be a JSON array of rental asset id strings.");
            }

            if (ids.Count > MaxRentalAssetIds)
            {
                throw new ArgumentException($"At most {MaxRentalAssetIds} rental assets are allowed.");
            }

            var existing = await TenantSettingStore.ExistingRentalAssetIdsAsync(
                dbContext, tenantId, ids.ToArray(), cancellationToken);
            var missing = ids.Where(id => !existing.Contains(id)).ToArray();
            if (missing.Length > 0)
            {
                throw new ArgumentException(
                    "Rental asset not found for this tenant: " + string.Join(", ", missing) + ".");
            }

            return TenantSettingValues.FormatRentalAssetIds(ids);
        }

        // Unreachable while AdminEditableKeys and this method stay in sync.
        throw new KeyNotFoundException("Setting key is not editable.");
    }

    private static void EnsureEditableKey(string key)
    {
        if (!TenantSettingKeys.AdminEditableKeys.Contains(key, StringComparer.Ordinal))
        {
            throw new KeyNotFoundException("Setting key is not editable.");
        }
    }

    private async Task EnsureTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            throw new KeyNotFoundException("Tenant not found.");
        }
    }
}
