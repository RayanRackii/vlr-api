using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Services;

public sealed class AssetRegistry(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IAssetService assetService,
    IAssetFamilyService assetFamilyService) : IAssetRegistry
{
    public async Task<IReadOnlyList<RegistryCategoryListItem>> ListCategoriesAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        return await dbContext.AssetCategories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new RegistryCategoryListItem(c.Id, c.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RegistryAssetListItem>> ListAssetsAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        return await dbContext.Assets
            .AsNoTracking()
            .OrderBy(a => a.Tag)
            .Select(a => new RegistryAssetListItem(
                a.Id,
                a.Name,
                a.Tag,
                a.UnitId,
                a.CategoryId,
                a.Status))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssetFamilyDetailResponse>> ListActiveFamiliesAsync(
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        return await assetFamilyService.ListActiveForTenantAsync(tenantId, cancellationToken);
    }

    public async Task<Asset> RequireAssetAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        return await dbContext.Assets
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Asset '{id}' was not found.");
    }

    public async Task<AssetCategory> RequireCategoryAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        return await dbContext.AssetCategories
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Asset category '{id}' was not found.");
    }

    public async Task<Asset> CreateRentableAsync(
        CreateRentableRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var created = await assetService.CreateRentableAsync(request, cancellationToken);
        return await LoadRentableAssetAsync(created.Id, cancellationToken);
    }

    public async Task<Asset> UpdateRentableAsync(
        Guid rentalAssetId,
        UpdateRentableRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var rental = await dbContext.RentalAssets
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(
                r => r.Id == rentalAssetId && r.IsActive && r.Asset.IsRentable,
                cancellationToken)
            ?? throw new KeyNotFoundException("Rentable was not found.");

        await assetService.UpdateRentableAsync(rental.AssetId, request, cancellationToken);
        return await LoadRentableAssetAsync(rental.AssetId, cancellationToken);
    }

    private async Task<Asset> LoadRentableAssetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        return await dbContext.Assets
            .Include(a => a.RentalConfiguration)
            .Include(a => a.Category)
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }
}
