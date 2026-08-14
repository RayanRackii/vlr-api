using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
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
                .ThenInclude(a => a.Category)
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
                .ThenInclude(a => a.Category)
            .FirstOrDefaultAsync(
                r => r.AssetId == assetId && r.IsActive && r.Asset.IsRentable,
                cancellationToken);

        return config is null ? null : ToResponse(config);
    }

    public async Task<RentalAssetResponse> UpdateSchedulePolicyAsync(
        Guid rentalAssetId,
        UpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var rental = await dbContext.RentalAssets
            .Include(r => r.Asset)
                .ThenInclude(a => a.Category)
            .FirstOrDefaultAsync(r => r.Id == rentalAssetId && r.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("Rentable was not found.");

        if (request.SchedulePolicy == SchedulePolicy.OpenHours)
        {
            ValidatePolicy(request.SchedulePolicy, request.OpenTime, request.CloseTime);
        }

        rental.SchedulePolicy = request.SchedulePolicy;
        rental.OpenTime = request.OpenTime;
        rental.CloseTime = request.CloseTime;
        rental.AllowedDurationMinutes = string.IsNullOrWhiteSpace(request.AllowedDurationMinutes)
            ? (request.SchedulePolicy == SchedulePolicy.OpenHours ? "60" : null)
            : request.AllowedDurationMinutes.Trim();
        rental.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(rental);
    }

    public async Task<BulkUpdateRentalSchedulePolicyResponseDto> UpdateSchedulePolicyBulkAsync(
        BulkUpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var ids = request.RentalAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            throw new ArgumentException("At least one rentable is required.");
        }

        ValidatePolicy(request.SchedulePolicy, request.OpenTime, request.CloseTime);

        var rentals = await dbContext.RentalAssets
            .Include(r => r.Asset)
                .ThenInclude(a => a.Category)
            .Where(r => ids.Contains(r.Id) && r.IsActive)
            .ToListAsync(cancellationToken);

        if (rentals.Count != ids.Count)
        {
            throw new KeyNotFoundException("One or more rentables were not found.");
        }

        var allowed = string.IsNullOrWhiteSpace(request.AllowedDurationMinutes)
            ? (request.SchedulePolicy == SchedulePolicy.OpenHours ? "60" : null)
            : request.AllowedDurationMinutes.Trim();

        foreach (var rental in rentals)
        {
            rental.SchedulePolicy = request.SchedulePolicy;
            rental.OpenTime = request.OpenTime;
            rental.CloseTime = request.CloseTime;
            rental.AllowedDurationMinutes = allowed;
            rental.Touch();
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var items = rentals
            .OrderBy(r => r.Asset.Name)
            .Select(ToResponse)
            .ToList();

        return new BulkUpdateRentalSchedulePolicyResponseDto(items.Count, items);
    }

    private static void ValidatePolicy(
        SchedulePolicy policy,
        TimeOnly? openTime,
        TimeOnly? closeTime)
    {
        if (policy != SchedulePolicy.OpenHours)
        {
            return;
        }

        if (openTime is null || closeTime is null)
        {
            throw new ArgumentException("OpenHours requires OpenTime and CloseTime.");
        }

        if (closeTime <= openTime)
        {
            throw new ArgumentException("CloseTime must be after OpenTime.");
        }
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
            rental.SchedulePolicy,
            rental.OpenTime,
            rental.CloseTime,
            rental.AllowedDurationMinutes,
            rental.Asset.CategoryId,
            rental.Asset.Category?.Name,
            rental.CreatedAt,
            rental.UpdatedAt);
}
