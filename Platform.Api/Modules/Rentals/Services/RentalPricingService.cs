using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalPricingService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IRentalPricingService
{
    private const int MaxAssetIds = 1000;
    private const int MaxPricings = 100;
    private const int MaxProduct = 10_000;

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

    public async Task<BulkApplyPricingsResponse> ApplyBulkAsync(
        BulkApplyPricingsRequest request,
        CancellationToken cancellationToken)
    {
        var assetIds = request.AssetIds ?? [];
        var pricings = request.Pricings ?? [];

        ValidateBulkPayload(assetIds, pricings, request.Replace);

        var tenantId = EnsureTenantContext();

        var rentals = new List<RentalAsset>(assetIds.Count);
        foreach (var assetId in assetIds)
        {
            rentals.Add(await EnsureRentableConfigAsync(assetId, cancellationToken));
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            if (request.Replace)
            {
                var rentalIds = rentals.Select(r => r.Id).ToList();
                var existing = await dbContext.RentalPricings
                    .Where(p => rentalIds.Contains(p.RentalAssetId))
                    .ToListAsync(cancellationToken);
                dbContext.RentalPricings.RemoveRange(existing);
            }
            else
            {
                foreach (var rental in rentals)
                {
                    foreach (var row in pricings)
                    {
                        await EnsureNoOverlapAsync(
                            rental.Id,
                            row.DayOfWeek,
                            row.StartTime,
                            row.EndTime,
                            excludePricingId: null,
                            cancellationToken);
                    }
                }
            }

            foreach (var rental in rentals)
            {
                foreach (var row in pricings)
                {
                    dbContext.RentalPricings.Add(new RentalPricing
                    {
                        TenantId = tenantId,
                        RentalAssetId = rental.Id,
                        DayOfWeek = row.DayOfWeek,
                        StartTime = row.StartTime,
                        EndTime = row.EndTime,
                        PricePerHour = RoundMoney(row.PricePerHour),
                        RequiresDeposit = row.RequiresDeposit,
                        DepositPercentage = RoundMoney(row.DepositPercentage),
                    });
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }

        return new BulkApplyPricingsResponse(rentals.Count, rentals.Count * pricings.Count);
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

    private static void ValidateBulkPayload(
        IReadOnlyList<Guid> assetIds,
        IReadOnlyList<BulkPricingRowDto> pricings,
        bool replace)
    {
        if (assetIds.Count == 0)
        {
            throw new ArgumentException("At least one asset is required.");
        }

        if (assetIds.Distinct().Count() != assetIds.Count)
        {
            throw new ArgumentException("AssetIds must not contain duplicates.");
        }

        if (assetIds.Count > MaxAssetIds)
        {
            throw new ArgumentException($"Cannot apply pricing to more than {MaxAssetIds} assets.");
        }

        if (pricings.Count > MaxPricings)
        {
            throw new ArgumentException($"Cannot apply more than {MaxPricings} pricing rows.");
        }

        if ((long)assetIds.Count * pricings.Count > MaxProduct)
        {
            throw new ArgumentException($"Bulk pricing cannot exceed {MaxProduct} created rows.");
        }

        if (!replace && pricings.Count == 0)
        {
            throw new ArgumentException("Pricings are required when Replace is false.");
        }

        foreach (var row in pricings)
        {
            ValidatePricingWindow(
                row.StartTime,
                row.EndTime,
                row.PricePerHour,
                row.DepositPercentage);
        }

        var windows = new HashSet<(DayOfWeek Day, TimeOnly Start, TimeOnly End)>();
        foreach (var row in pricings)
        {
            if (!windows.Add((row.DayOfWeek, row.StartTime, row.EndTime)))
            {
                throw new ArgumentException("Pricings contain duplicate windows for the same day.");
            }
        }

        for (var i = 0; i < pricings.Count; i++)
        {
            for (var j = i + 1; j < pricings.Count; j++)
            {
                var left = pricings[i];
                var right = pricings[j];
                if (left.DayOfWeek != right.DayOfWeek)
                {
                    continue;
                }

                if (left.StartTime < right.EndTime && left.EndTime > right.StartTime)
                {
                    throw new InvalidOperationException(
                        $"Pricing window overlaps another rule in the payload on {left.DayOfWeek} " +
                        $"({left.StartTime:HH\\:mm}-{left.EndTime:HH\\:mm}).");
                }
            }
        }
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
