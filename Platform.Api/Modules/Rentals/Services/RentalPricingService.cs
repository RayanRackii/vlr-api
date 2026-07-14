using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalPricingService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IRentalPricingService
{
    public async Task<IReadOnlyList<RentalPricingResponseDto>> GetByAssetIdAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        var rental = await EnsureRentableConfigAsync(assetId, cancellationToken);

        var pricings = await dbContext.RentalPricings
            .AsNoTracking()
            .Where(p => p.RentalAssetId == rental.Id)
            .OrderBy(p => p.DayOfWeek)
            .ThenBy(p => p.StartTime)
            .ToListAsync(cancellationToken);

        return pricings.Select(p => ToResponse(p, assetId)).ToList();
    }

    public async Task<RentalPricingResponseDto> CreateAsync(
        Guid assetId,
        CreateRentalPricingDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var rental = await EnsureRentableConfigAsync(assetId, cancellationToken);
        ValidatePricingWindow(request.StartTime, request.EndTime, request.PricePerHour, request.DepositPercentage);

        await EnsureNoOverlapAsync(
            rental.Id,
            request.DayOfWeek,
            request.StartTime,
            request.EndTime,
            excludePricingId: null,
            cancellationToken);

        var pricing = new RentalPricing
        {
            TenantId = tenantId,
            RentalAssetId = rental.Id,
            DayOfWeek = request.DayOfWeek,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            PricePerHour = RoundMoney(request.PricePerHour),
            RequiresDeposit = request.RequiresDeposit,
            DepositPercentage = RoundMoney(request.DepositPercentage),
        };

        dbContext.RentalPricings.Add(pricing);
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(pricing, assetId);
    }

    public async Task<RentalPricingResponseDto?> UpdateAsync(
        Guid assetId,
        Guid pricingId,
        UpdateRentalPricingDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        var rental = await EnsureRentableConfigAsync(assetId, cancellationToken);
        ValidatePricingWindow(request.StartTime, request.EndTime, request.PricePerHour, request.DepositPercentage);

        var pricing = await dbContext.RentalPricings
            .FirstOrDefaultAsync(
                p => p.Id == pricingId && p.RentalAssetId == rental.Id,
                cancellationToken);

        if (pricing is null)
        {
            return null;
        }

        await EnsureNoOverlapAsync(
            rental.Id,
            request.DayOfWeek,
            request.StartTime,
            request.EndTime,
            excludePricingId: pricingId,
            cancellationToken);

        pricing.DayOfWeek = request.DayOfWeek;
        pricing.StartTime = request.StartTime;
        pricing.EndTime = request.EndTime;
        pricing.PricePerHour = RoundMoney(request.PricePerHour);
        pricing.RequiresDeposit = request.RequiresDeposit;
        pricing.DepositPercentage = RoundMoney(request.DepositPercentage);
        pricing.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(pricing, assetId);
    }

    public async Task<bool> DeleteAsync(
        Guid assetId,
        Guid pricingId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        var rental = await EnsureRentableConfigAsync(assetId, cancellationToken);

        var pricing = await dbContext.RentalPricings
            .FirstOrDefaultAsync(
                p => p.Id == pricingId && p.RentalAssetId == rental.Id,
                cancellationToken);

        if (pricing is null)
        {
            return false;
        }

        dbContext.RentalPricings.Remove(pricing);
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    private async Task<RentalAsset> EnsureRentableConfigAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var rental = await dbContext.RentalAssets
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(r => r.AssetId == assetId, cancellationToken);

        if (rental is null || !rental.Asset.IsRentable || !rental.IsActive)
        {
            throw new KeyNotFoundException(
                $"Rentable configuration for asset '{assetId}' was not found.");
        }

        return rental;
    }

    private async Task EnsureNoOverlapAsync(
        Guid rentalAssetId,
        DayOfWeek dayOfWeek,
        TimeOnly startTime,
        TimeOnly endTime,
        Guid? excludePricingId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.RentalPricings
            .AsNoTracking()
            .Where(p => p.RentalAssetId == rentalAssetId
                        && p.DayOfWeek == dayOfWeek
                        && p.StartTime < endTime
                        && p.EndTime > startTime);

        if (excludePricingId is Guid excludedId)
        {
            query = query.Where(p => p.Id != excludedId);
        }

        var overlap = await query.FirstOrDefaultAsync(cancellationToken);

        if (overlap is not null)
        {
            throw new InvalidOperationException(
                $"Pricing window overlaps an existing rule on {dayOfWeek} " +
                $"({overlap.StartTime:HH\\:mm}-{overlap.EndTime:HH\\:mm}).");
        }
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static void ValidatePricingWindow(
        TimeOnly startTime,
        TimeOnly endTime,
        decimal pricePerHour,
        decimal depositPercentage)
    {
        if (endTime <= startTime)
        {
            throw new ArgumentException("EndTime must be greater than StartTime.");
        }

        if (pricePerHour < 0)
        {
            throw new ArgumentException("PricePerHour cannot be negative.");
        }

        if (depositPercentage < 0 || depositPercentage > 100)
        {
            throw new ArgumentException("DepositPercentage must be between 0 and 100.");
        }
    }

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static RentalPricingResponseDto ToResponse(RentalPricing pricing, Guid assetId) =>
        new(
            pricing.Id,
            pricing.TenantId,
            assetId,
            pricing.RentalAssetId,
            pricing.DayOfWeek,
            pricing.StartTime,
            pricing.EndTime,
            pricing.PricePerHour,
            pricing.RequiresDeposit,
            pricing.DepositPercentage,
            pricing.CreatedAt,
            pricing.UpdatedAt);
}
