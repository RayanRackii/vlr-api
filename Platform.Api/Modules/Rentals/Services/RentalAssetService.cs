using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalAssetService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IRentalAssetService
{
    public async Task<IReadOnlyList<RentalAssetResponse>> ListRentableAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var configs = await dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
            .Where(r => r.IsActive && r.Asset.IsRentable && r.Asset.Status != Platform.Core.Domain.Enums.AssetStatus.Inactive)
            .OrderBy(r => r.Asset.Name)
            .ToListAsync(cancellationToken);

        return configs.Select(ToResponse).ToList();
    }

    public async Task<RentalAssetResponse?> GetByAssetIdAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var config = await dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(
                r => r.AssetId == assetId && r.IsActive && r.Asset.IsRentable,
                cancellationToken);

        return config is null ? null : ToResponse(config);
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static RentalAssetResponse ToResponse(RentalAsset rental) =>
        new(
            rental.Id,
            rental.AssetId,
            rental.TenantId,
            rental.Asset.UnitId,
            rental.Asset.Name,
            rental.Type,
            rental.TotalQuantity,
            rental.IsActive,
            rental.CreatedAt,
            rental.UpdatedAt);
}
