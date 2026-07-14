using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class ReservationService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IReservationService
{
    private static readonly ReservationStatus[] BlockingStatuses =
    [
        ReservationStatus.PendingDeposit,
        ReservationStatus.Confirmed
    ];

    public async Task<CheckAvailabilityResponseDto> CheckAvailabilityAsync(
        CheckAvailabilityRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        ValidateTimeRange(request.Date, request.StartTime, request.EndTime);

        var quantity = request.Quantity < 1 ? 1 : request.Quantity;
        var (start, end) = ToDateTimeRange(request.Date, request.StartTime, request.EndTime);

        var rental = await dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
            .FirstOrDefaultAsync(
                r => r.AssetId == request.AssetId && r.IsActive && r.Asset.IsRentable,
                cancellationToken);

        if (rental is null)
        {
            return new CheckAvailabilityResponseDto(
                IsAvailable: false,
                RequestedQuantity: quantity,
                AvailableQuantity: 0,
                EstimatedTotalAmount: null,
                Reason: "Rentable asset was not found or is inactive.");
        }

        var reservedQuantity = await GetReservedQuantityAsync(
            rental.Id,
            start,
            end,
            excludeReservationId: null,
            cancellationToken);

        var availableQuantity = Math.Max(0, rental.TotalQuantity - reservedQuantity);
        var isAvailable = rental.Type switch
        {
            RentalAssetType.Location => availableQuantity >= 1,
            RentalAssetType.Good => availableQuantity >= quantity,
            _ => false
        };

        decimal? estimatedTotal = null;
        string? reason = null;

        if (!isAvailable)
        {
            reason = rental.Type == RentalAssetType.Location
                ? "Location is already reserved for the requested interval."
                : $"Insufficient quantity. Available: {availableQuantity}, requested: {quantity}.";
        }
        else
        {
            try
            {
                var unitPrice = await ResolveHourlyPriceAsync(
                    rental.Id,
                    request.Date,
                    request.StartTime,
                    request.EndTime,
                    cancellationToken);
                var hours = (decimal)(end - start).TotalHours;
                estimatedTotal = RoundMoney(unitPrice * hours * quantity);
            }
            catch (InvalidOperationException ex)
            {
                reason = ex.Message;
            }
        }

        return new CheckAvailabilityResponseDto(
            IsAvailable: isAvailable && estimatedTotal is not null,
            RequestedQuantity: quantity,
            AvailableQuantity: availableQuantity,
            EstimatedTotalAmount: estimatedTotal,
            Reason: isAvailable && estimatedTotal is not null ? null : reason);
    }

    public async Task<ReservationResponseDto> CreateReservationAsync(
        Guid customerId,
        CreateReservationRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        ValidateTimeRange(request.Date, request.StartTime, request.EndTime);

        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("At least one reservation item is required.");
        }

        var customer = await dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Authenticated customer was not found.");

        if (customer.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("Customer does not belong to the current tenant.");
        }

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);

        var (start, end) = ToDateTimeRange(request.Date, request.StartTime, request.EndTime);
        var hours = (decimal)(end - start).TotalHours;

        var whatsAppSnapshot = string.IsNullOrWhiteSpace(customer.Phone)
            ? string.Empty
            : customer.Phone;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var reservation = new Reservation
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                CustomerId = customer.Id,
                CustomerName = customer.Name,
                CustomerWhatsApp = whatsAppSnapshot,
                StartDateTime = start,
                EndDateTime = end,
                Status = ReservationStatus.PendingDeposit,
                TotalAmount = 0m,
                DepositPaid = 0m,
            };

            decimal totalAmount = 0m;
            var itemResponses = new List<(ReservationItem Item, Guid AssetId, string AssetName)>();

            foreach (var itemRequest in request.Items)
            {
                var quantity = itemRequest.Quantity < 1 ? 1 : itemRequest.Quantity;

                var rental = await dbContext.RentalAssets
                    .Include(r => r.Asset)
                    .FirstOrDefaultAsync(
                        r => r.AssetId == itemRequest.AssetId && r.IsActive && r.Asset.IsRentable,
                        cancellationToken)
                    ?? throw new KeyNotFoundException(
                        $"Rentable asset '{itemRequest.AssetId}' was not found.");

                if (rental.Asset.UnitId != request.UnitId)
                {
                    throw new InvalidOperationException(
                        $"Asset '{rental.Asset.Name}' does not belong to the given unit.");
                }

                var reservedQuantity = await GetReservedQuantityAsync(
                    rental.Id,
                    start,
                    end,
                    excludeReservationId: null,
                    cancellationToken);

                var availableQuantity = rental.TotalQuantity - reservedQuantity;

                if (rental.Type == RentalAssetType.Location && reservedQuantity > 0)
                {
                    throw new InvalidOperationException(
                        $"Location '{rental.Asset.Name}' is not available for the requested interval.");
                }

                if (rental.Type == RentalAssetType.Good && quantity > availableQuantity)
                {
                    throw new InvalidOperationException(
                        $"Insufficient quantity for '{rental.Asset.Name}'. Available: {availableQuantity}, requested: {quantity}.");
                }

                var unitPrice = await ResolveHourlyPriceAsync(
                    rental.Id,
                    request.Date,
                    request.StartTime,
                    request.EndTime,
                    cancellationToken);

                var subTotal = RoundMoney(unitPrice * hours * quantity);
                totalAmount += subTotal;

                var item = new ReservationItem
                {
                    TenantId = tenantId,
                    ReservationId = reservation.Id,
                    RentalAssetId = rental.Id,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    SubTotal = subTotal,
                };

                reservation.AddItem(item);
                itemResponses.Add((item, rental.AssetId, rental.Asset.Name));
            }

            reservation.TotalAmount = RoundMoney(totalAmount);

            dbContext.Reservations.Add(reservation);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToResponse(reservation, itemResponses);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<int> GetReservedQuantityAsync(
        Guid rentalAssetId,
        DateTimeOffset start,
        DateTimeOffset end,
        Guid? excludeReservationId,
        CancellationToken cancellationToken)
    {
        var query =
            from item in dbContext.ReservationItems.AsNoTracking()
            join reservation in dbContext.Reservations.AsNoTracking()
                on item.ReservationId equals reservation.Id
            where item.RentalAssetId == rentalAssetId
                  && BlockingStatuses.Contains(reservation.Status)
                  && reservation.StartDateTime < end
                  && reservation.EndDateTime > start
            select new { item.Quantity, reservation.Id };

        if (excludeReservationId is Guid excludedId)
        {
            query = query.Where(x => x.Id != excludedId);
        }

        return await query.SumAsync(x => x.Quantity, cancellationToken);
    }

    private async Task<decimal> ResolveHourlyPriceAsync(
        Guid rentalAssetId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var dayOfWeek = date.DayOfWeek;

        var pricing = await dbContext.RentalPricings
            .AsNoTracking()
            .Where(p => p.RentalAssetId == rentalAssetId && p.DayOfWeek == dayOfWeek)
            .Where(p => p.StartTime <= startTime && p.EndTime >= endTime)
            .OrderBy(p => p.PricePerHour)
            .FirstOrDefaultAsync(cancellationToken);

        if (pricing is null)
        {
            throw new InvalidOperationException(
                "No rental pricing covers the requested day and time window.");
        }

        return pricing.PricePerHour;
    }

    private async Task EnsureUnitExistsAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Units
            .AsNoTracking()
            .AnyAsync(u => u.Id == unitId, cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException($"Unit '{unitId}' was not found.");
        }
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static void ValidateTimeRange(DateOnly date, TimeOnly startTime, TimeOnly endTime)
    {
        _ = date;

        if (endTime <= startTime)
        {
            throw new ArgumentException("EndTime must be greater than StartTime.");
        }
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ToDateTimeRange(
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime)
    {
        var start = new DateTimeOffset(date.ToDateTime(startTime, DateTimeKind.Unspecified), TimeSpan.Zero);
        var end = new DateTimeOffset(date.ToDateTime(endTime, DateTimeKind.Unspecified), TimeSpan.Zero);
        return (start, end);
    }

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static ReservationResponseDto ToResponse(
        Reservation reservation,
        IReadOnlyList<(ReservationItem Item, Guid AssetId, string AssetName)> items) =>
        new(
            reservation.Id,
            reservation.TenantId,
            reservation.UnitId,
            reservation.CustomerId,
            reservation.CustomerName,
            reservation.CustomerWhatsApp,
            reservation.StartDateTime,
            reservation.EndDateTime,
            reservation.Status,
            reservation.TotalAmount,
            reservation.DepositPaid,
            reservation.CreatedAt,
            items.Select(x => new ReservationItemResponseDto(
                x.Item.Id,
                x.AssetId,
                x.Item.RentalAssetId,
                x.AssetName,
                x.Item.Quantity,
                x.Item.UnitPrice,
                x.Item.SubTotal)).ToList());
}
