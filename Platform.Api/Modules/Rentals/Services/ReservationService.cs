using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class ReservationService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard) : IReservationService
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
        else if (rental.SchedulePolicy == SchedulePolicy.SlotGrid
                 && !await IsSlotGridIntervalOpenAsync(
                     rental.Id,
                     request.Date,
                     request.StartTime,
                     request.EndTime,
                     cancellationToken))
        {
            isAvailable = false;
            reason = "This interval is not open for booking.";
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
        await trialGuard.EnsureWritableAsync(cancellationToken);
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
            var requiresDeposit = false;
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

                if (rental.SchedulePolicy == SchedulePolicy.SlotGrid
                    && !await IsSlotGridIntervalOpenAsync(
                        rental.Id,
                        request.Date,
                        request.StartTime,
                        request.EndTime,
                        cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"'{rental.Asset.Name}' is not open for the requested interval.");
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

                requiresDeposit |= rental.RequiresDeposit;

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
            reservation.OpenAccordingToPaymentPolicy(requiresDeposit);

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

    public async Task<IReadOnlyList<ReservationResponseDto>> ListMineAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var reservations = await dbContext.Reservations
            .AsNoTracking()
            .Include(r => r.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(a => a.Asset)
            .Where(r => r.CustomerId == customerId)
            .OrderByDescending(r => r.StartDateTime)
            .ToListAsync(cancellationToken);

        return reservations
            .Select(r => ToResponse(
                r,
                r.Items
                    .Select(i => (
                        i,
                        i.RentalAsset.AssetId,
                        i.RentalAsset.Asset.Name))
                    .ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<ReservationResponseDto>> ListAdminAsync(
        DateOnly? from,
        DateOnly? to,
        ReservationStatus? status,
        Guid? assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var query = dbContext.Reservations
            .AsNoTracking()
            .Include(r => r.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(a => a.Asset)
            .AsQueryable();

        if (from is DateOnly fromDate)
        {
            var fromStart = new DateTimeOffset(
                fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                TimeSpan.Zero);
            query = query.Where(r => r.EndDateTime >= fromStart);
        }

        if (to is DateOnly toDate)
        {
            var toEnd = new DateTimeOffset(
                toDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Unspecified),
                TimeSpan.Zero);
            query = query.Where(r => r.StartDateTime <= toEnd);
        }

        if (status is ReservationStatus statusFilter)
        {
            query = query.Where(r => r.Status == statusFilter);
        }

        if (assetId is Guid filterAssetId)
        {
            query = query.Where(r =>
                r.Items.Any(i => i.RentalAsset.AssetId == filterAssetId));
        }

        var reservations = await query
            .OrderByDescending(r => r.StartDateTime)
            .ToListAsync(cancellationToken);

        return reservations
            .Select(r => ToResponse(
                r,
                r.Items
                    .Select(i => (
                        i,
                        i.RentalAsset.AssetId,
                        i.RentalAsset.Asset.Name))
                    .ToList()))
            .ToList();
    }

    public async Task<ReservationResponseDto> ConfirmAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        var reservation = await dbContext.Reservations
            .Include(r => r.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(a => a.Asset)
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Reservation '{reservationId}' was not found.");

        if (reservation.Status is ReservationStatus.Canceled or ReservationStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Cannot confirm a reservation in status '{reservation.Status}'.");
        }

        if (reservation.Status == ReservationStatus.Confirmed)
        {
            return ToResponseFromEntity(reservation);
        }

        if (reservation.Status != ReservationStatus.PendingDeposit)
        {
            throw new InvalidOperationException(
                $"Only pending reservations can be confirmed (current: '{reservation.Status}').");
        }

        reservation.Status = ReservationStatus.Confirmed;
        reservation.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponseFromEntity(reservation);
    }

    public async Task<ReservationResponseDto> CancelAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);

        var reservation = await dbContext.Reservations
            .Include(r => r.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(a => a.Asset)
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Reservation '{reservationId}' was not found.");

        if (reservation.Status == ReservationStatus.Canceled)
        {
            return ToResponseFromEntity(reservation);
        }

        if (reservation.Status == ReservationStatus.Completed)
        {
            throw new InvalidOperationException("Cannot cancel a completed reservation.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            reservation.Status = ReservationStatus.Canceled;
            reservation.Touch();

            var linkedSlots = await dbContext.Slots
                .Where(s => s.ReservationId == reservationId)
                .ToListAsync(cancellationToken);

            foreach (var slot in linkedSlots)
            {
                slot.MarkAvailable();
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToResponseFromEntity(reservation);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static ReservationResponseDto ToResponseFromEntity(Reservation reservation) =>
        ToResponse(
            reservation,
            reservation.Items
                .Select(i => (
                    i,
                    i.RentalAsset.AssetId,
                    i.RentalAsset.Asset.Name))
                .ToList());

    private async Task<bool> IsSlotGridIntervalOpenAsync(
        Guid rentalAssetId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var persisted = await dbContext.Slots
            .AsNoTracking()
            .Include(s => s.OccupancyKind)
            .FirstOrDefaultAsync(
                s => s.RentalAssetId == rentalAssetId
                     && s.Date == date
                     && s.StartTime == startTime,
                cancellationToken);

        if (persisted is not null)
        {
            return persisted.Status == SlotStatus.Available
                   && persisted.EndTime == endTime
                   && persisted.OccupancyKind.IsBookableByCustomer;
        }

        var template = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.OccupancyKind)
            .FirstOrDefaultAsync(
                t => t.RentalAssetId == rentalAssetId
                     && t.IsActive
                     && t.DayOfWeek == date.DayOfWeek
                     && t.StartTime == startTime
                     && t.EndTime == endTime,
                cancellationToken);

        return template is not null && template.OccupancyKind.IsBookableByCustomer;
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
