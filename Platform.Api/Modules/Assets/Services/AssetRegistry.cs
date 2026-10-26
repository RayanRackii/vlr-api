using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Serialization;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Services;

public sealed class AssetRegistry(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IAssetService assetService,
    IAssetFamilyService assetFamilyService,
    IAssetCategoryService assetCategoryService) : IAssetRegistry
{
    public async Task<RegistryCategoryListItem> CreateCategoryAsync(
        CreateAssetCategoryRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var category = await assetCategoryService.CreateAsync(request, cancellationToken);
        return new RegistryCategoryListItem(category.Id, category.Name);
    }

    public async Task<IReadOnlyList<RegistryCategoryListItem>> ListCategoriesAsync(
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        return await dbContext.AssetCategories
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .Select(c => new RegistryCategoryListItem(c.Id, c.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RegistryAssetListItem>> ListAssetsAsync(
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        return await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
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
        var tenantId = EnsureTenantContext();

        return await dbContext.Assets
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asset '{id}' was not found.");
    }

    public async Task<AssetCategory> RequireCategoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        return await dbContext.AssetCategories
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asset category '{id}' was not found.");
    }

    public async Task<Asset> CreateRentableAsync(
        CreateRentableRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var cutoff = ValidateCutoff(request.CancelCutoffHours);
        var created = await assetService.CreateRentableAsync(request, cancellationToken);
        await ApplyCancelCutoffAsync(created.Id, request.CancelCutoffHours, cutoff, cancellationToken);
        return await LoadRentableAssetAsync(created.Id, cancellationToken);
    }

    public async Task<Asset> UpdateRentableAsync(
        Guid rentalAssetId,
        UpdateRentableRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var rental = await dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(
                r => r.Id == rentalAssetId && r.TenantId == tenantId && r.Asset.TenantId == tenantId && r.IsActive && r.Asset.IsRentable,
                cancellationToken)
            ?? throw new KeyNotFoundException("Rentable was not found.");

        var cutoff = ValidateCutoff(request.CancelCutoffHours);
        await assetService.UpdateRentableAsync(rental.AssetId, request, cancellationToken);
        await ApplyCancelCutoffAsync(rental.AssetId, request.CancelCutoffHours, cutoff, cancellationToken);
        return await LoadRentableAssetAsync(rental.AssetId, cancellationToken);
    }

    private static int? ValidateCutoff(JsonOptional<int> requested) =>
        !requested.IsSpecified ? null
        : requested.Value is < 0 or > 8760
            // Same rule/message as Rentals PolicyResolver.ValidateCancelCutoffHours (module boundary).
            ? throw new ArgumentException("Cancellation cutoff must be an integer from 0 to 8760, or null for no cutoff.")
            : requested.Value;

    // The drawer sends cancelCutoffHours on PUT/POST /api/rental-assets; before this it was
    // silently dropped by model binding (field missing from the DTO) and the API answered 200.
    private async Task ApplyCancelCutoffAsync(
        Guid assetId, JsonOptional<int> requested, int? cutoff, CancellationToken cancellationToken)
    {
        if (!requested.IsSpecified) return;
        var tenantId = EnsureTenantContext();
        var rental = await dbContext.RentalAssets
            .FirstOrDefaultAsync(r => r.AssetId == assetId && r.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Rentable was not found.");
        if (rental.CancelCutoffHours == cutoff) return;
        rental.CancelCutoffHours = cutoff;
        rental.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Asset> LoadRentableAssetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        return await dbContext.Assets
            .Include(a => a.RentalConfiguration)
            .Include(a => a.Category)
            .FirstOrDefaultAsync(a => a.Id == assetId && a.TenantId == tenantProvider.TenantId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId is { } id && id != Guid.Empty
            ? id : throw new UnauthorizedAccessException("Tenant context is required.");
    }
}
