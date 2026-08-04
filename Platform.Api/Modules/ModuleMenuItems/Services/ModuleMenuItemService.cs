using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.ModuleMenuItems.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ModuleMenuItems.Services;

public sealed class ModuleMenuItemService(AppDbContext dbContext) : IModuleMenuItemService
{
    public async Task<IReadOnlyList<ModuleMenuItemDto>> GetPublicMenuBySubdomainAsync(
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

        var activeModules = await dbContext.TenantModules
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenant.Id && m.IsActive)
            .Select(m => m.ModuleName)
            .ToListAsync(cancellationToken);

        var activeSet = activeModules.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = await dbContext.TenantModuleMenuItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Include(i => i.RentalAsset)
            .Where(i => i.TenantId == tenant.Id && i.IsActive)
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Label)
            .ToListAsync(cancellationToken);

        return items
            .Where(i => activeSet.Contains(i.ModuleName))
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<ModuleMenuItemDto>> ListForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await EnsureTenantExistsAsync(tenantId, cancellationToken);

        var items = await dbContext.TenantModuleMenuItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Include(i => i.RentalAsset)
            .Where(i => i.TenantId == tenantId)
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Label)
            .ToListAsync(cancellationToken);

        return items.Select(ToDto).ToList();
    }

    public async Task<ModuleMenuItemDto> CreateAsync(
        Guid tenantId,
        UpsertModuleMenuItemRequestDto request,
        CancellationToken cancellationToken)
    {
        await EnsureTenantExistsAsync(tenantId, cancellationToken);

        if (!PlatformModules.TryNormalize(request.ModuleName, out var moduleName))
        {
            throw new ArgumentException($"Unknown module '{request.ModuleName}'.");
        }

        await EnsureModuleActiveAsync(tenantId, moduleName, cancellationToken);
        await EnsureRentalAssetAsync(tenantId, moduleName, request.RentalAssetId, cancellationToken);

        var entity = new TenantModuleMenuItem(
            tenantId,
            moduleName,
            request.Label,
            request.SortOrder,
            request.IsActive,
            request.RentalAssetId);

        dbContext.TenantModuleMenuItems.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await LoadDtoAsync(entity.Id, cancellationToken);
    }

    public async Task<ModuleMenuItemDto> UpdateAsync(
        Guid tenantId,
        Guid itemId,
        UpdateModuleMenuItemRequestDto request,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.TenantModuleMenuItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == itemId && i.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Menu item not found.");

        await EnsureModuleActiveAsync(tenantId, entity.ModuleName, cancellationToken);
        await EnsureRentalAssetAsync(
            tenantId,
            entity.ModuleName,
            request.RentalAssetId,
            cancellationToken);

        entity.Update(request.Label, request.SortOrder, request.IsActive, request.RentalAssetId);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await LoadDtoAsync(entity.Id, cancellationToken);
    }

    public async Task DeleteAsync(
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.TenantModuleMenuItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == itemId && i.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Menu item not found.");

        dbContext.TenantModuleMenuItems.Remove(entity);
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

    private async Task EnsureModuleActiveAsync(
        Guid tenantId,
        string moduleName,
        CancellationToken cancellationToken)
    {
        var active = await dbContext.TenantModules
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(
                m => m.TenantId == tenantId
                     && m.ModuleName == moduleName
                     && m.IsActive,
                cancellationToken);

        if (!active)
        {
            throw new ArgumentException(
                $"Module '{moduleName}' is not active for this tenant.");
        }
    }

    private async Task EnsureRentalAssetAsync(
        Guid tenantId,
        string moduleName,
        Guid? rentalAssetId,
        CancellationToken cancellationToken)
    {
        if (!rentalAssetId.HasValue)
        {
            return;
        }

        if (moduleName != PlatformModules.Rentals)
        {
            throw new ArgumentException(
                "RentalAssetId is only allowed when ModuleName is rentals.");
        }

        var ok = await dbContext.RentalAssets
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(
                a => a.Id == rentalAssetId.Value && a.TenantId == tenantId && a.IsActive,
                cancellationToken);

        if (!ok)
        {
            throw new ArgumentException("Rental asset not found for this tenant.");
        }
    }

    private async Task<ModuleMenuItemDto> LoadDtoAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.TenantModuleMenuItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Include(i => i.RentalAsset)
            .FirstAsync(i => i.Id == id, cancellationToken);

        return ToDto(entity);
    }

    private static ModuleMenuItemDto ToDto(TenantModuleMenuItem item) =>
        new(
            item.Id,
            item.ModuleName,
            item.Label,
            item.SortOrder,
            item.IsActive,
            item.RentalAssetId,
            item.RentalAsset?.AssetId);
}
