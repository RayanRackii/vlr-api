using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Api.DomainEvents;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Events;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class ReservationService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITrialGuard trialGuard,
    IReservationQueueService reservationQueueService,
    IDomainEventDispatcher<ReservationEvent> domainEvents,
    INotificationOutboxScheduler outboxScheduler,
    TimeProvider timeProvider,
    PolicyResolver? policyResolver = null) : IReservationService
{
    private readonly PolicyResolver policies = policyResolver ?? new(dbContext);

    private static readonly ReservationStatus[] BlockingStatuses = AvailabilityReadModel.BlockingStatuses;

    public async Task<CheckAvailabilityResponseDto> CheckAvailabilityAsync(
        CheckAvailabilityRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        ValidateTimeRange(request.Date, request.StartTime, request.EndTime, request.EndDate);

        var quantity = request.Quantity < 1 ? 1 : request.Quantity;
        var zone = await ResolveBookingZoneAsync(tenantId, cancellationToken);
        var (start, end) = ToDateTimeRange(request.Date, request.StartTime, request.EndTime, zone, request.EndDate);
        var calendarDays = request.EndDate > request.Date ? request.EndDate.Value.DayNumber - request.Date.DayNumber + 1 : (int?)null;

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

        if (ReservationBookingRules.StartsBeforeNow(start, timeProvider))
        {
            return new CheckAvailabilityResponseDto(
                IsAvailable: false,
                RequestedQuantity: quantity,
                AvailableQuantity: 0,
                EstimatedTotalAmount: null,
                Reason: ReservationBookingRules.PastStartMessage);
        }

        var today = BookingHorizon.CivilToday(timeProvider.GetUtcNow(), zone);
        var tenantPolicy = await policies.ReadTenantAsync(tenantId, cancellationToken);
        var categories = await policies.ReadCategoriesAsync(tenantId, cancellationToken);
        BookingHorizon.Ensure(
            request.Date,
            today,
            PolicyResolver.Resolve(rental, categories.GetValueOrDefault(rental.Asset.CategoryId), tenantPolicy).MaxAdvanceBookingDays);
        BookingHorizon.Ensure(
            request.EndDate ?? request.Date,
            today,
            PolicyResolver.Resolve(rental, categories.GetValueOrDefault(rental.Asset.CategoryId), tenantPolicy).MaxAdvanceBookingDays);

        RentalPricing? multiDayPricing = null;
        if (calendarDays.HasValue)
        {
            try
            {
                multiDayPricing = await ResolveMultiDayPricingAsync(rental, request.Date, request.EndDate!.Value,
                    request.StartTime, request.EndTime, cancellationToken);
            }
            catch (MultiDayUnavailableException ex)
            {
                return new CheckAvailabilityResponseDto(
                    IsAvailable: false,
                    RequestedQuantity: quantity,
                    AvailableQuantity: 0,
                    EstimatedTotalAmount: null,
                    Reason: ex.Message);
            }
        }

        var reservedQuantity = await GetReservedQuantityAsync(
            tenantId,
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
        PricingUnit? pricingUnit = null;
        string? reason = null;
        IReadOnlyList<(TimeOnly Start, TimeOnly End)>? slotGridWindows = null;

        if (!isAvailable)
        {
            reason = rental.Type == RentalAssetType.Location
                ? "Location is already reserved for the requested interval."
                : $"Insufficient quantity. Available: {availableQuantity}, requested: {quantity}.";
        }
        else if (!calendarDays.HasValue && rental.SchedulePolicy == SchedulePolicy.OpenHours
                 && OpenHoursRules.ValidateInterval(
                     rental, request.StartTime, request.EndTime, rental.Asset.Name) is { } openHoursError)
        {
            isAvailable = false;
            reason = openHoursError;
        }
        else if (rental.SchedulePolicy == SchedulePolicy.SlotGrid)
        {
            var plan = await ResolveSlotGridWindowsAsync(
                rental, request.Date, request.StartTime, request.EndTime, cancellationToken);
            if (!plan.IsOpen)
            {
                isAvailable = false;
                reason = "This interval is not open for booking.";
            }
            else
            {
                slotGridWindows = plan.Windows;
            }
        }

        if (isAvailable && reason is null)
        {
            try
            {
                var hours = (decimal)(end - start).TotalHours;
                if (slotGridWindows is { Count: > 1 } windows)
                {
                    var (total, unit, _) = await ResolveSlotGridPricingAsync(
                        rental.Id, request.Date, request.StartTime, request.EndTime,
                        windows, hours, quantity, zone, cancellationToken);
                    estimatedTotal = total;
                    pricingUnit = unit;
                }
                else
                {
                    var pricing = multiDayPricing ?? await ResolvePricingAsync(
                        rental.Id,
                        request.Date,
                        request.StartTime,
                        request.EndTime,
                        hours,
                        quantity,
                        cancellationToken);
                    estimatedTotal = BookingOffer.Total(pricing, hours, quantity, calendarDays);
                    pricingUnit = pricing.PricingUnit;
                }
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
            Reason: isAvailable && estimatedTotal is not null ? null : reason,
            PricingUnit: pricingUnit);
    }

    public async Task<ReservationResponseDto> CreateReservationAsync(
        Guid customerId,
        CreateReservationRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        await domainEvents.EnsureReadyAsync(cancellationToken);
        ValidateTimeRange(request.Date, request.StartTime, request.EndTime, request.EndDate);

        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("At least one reservation item is required.");
        }

        if (request.Items.Select(i => i.AssetId).Distinct().Count() != request.Items.Count)
        {
            throw new ArgumentException("Each assetId may appear only once per reservation.");
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

        var zone = await ResolveBookingZoneAsync(tenantId, cancellationToken);
        var (start, end) = ToDateTimeRange(request.Date, request.StartTime, request.EndTime, zone, request.EndDate);
        var calendarDays = request.EndDate > request.Date ? request.EndDate.Value.DayNumber - request.Date.DayNumber + 1 : (int?)null;
        ReservationBookingRules.EnsureStartNotBeforeNow(start, timeProvider);
        var today = BookingHorizon.CivilToday(timeProvider.GetUtcNow(), zone);
        var hours = (decimal)(end - start).TotalHours;

        var whatsAppSnapshot = string.IsNullOrWhiteSpace(customer.Phone)
            ? string.Empty
            : customer.Phone;

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            // A multi-day reservation counts once, against its start civil date only.
            await BookingPolicyEnforcement.EnsureCustomerCanBookAsync(
                dbContext,
                tenantId,
                customer.Id,
                request.Date,
                cancellationToken);

            var assetIds = request.Items
                .Select(item => item.AssetId)
                .Distinct()
                .ToList();

            var rentalAssetsToLock = await dbContext.RentalAssets
                .Where(r => r.TenantId == tenantId && assetIds.Contains(r.AssetId) && r.IsActive)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);

            await RentalAssetLocks.LockByRentalAssetIdsAsync(
                dbContext,
                tenantId,
                rentalAssetsToLock,
                cancellationToken);

            var tenantHorizon = await BookingHorizon.ReadTenantDaysAsync(
                dbContext, tenantId, cancellationToken);
            await BookingHorizon.EnsureAssetsAsync(
                dbContext,
                tenantId,
                request.Items.Select(item => item.AssetId).ToList(),
                // The last day is at or after the start, so this enforces both endpoints.
                request.EndDate ?? request.Date,
                today,
                tenantHorizon,
                cancellationToken);

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
            var validatedSlotIds = new List<Guid>();
            var queuedLocations = new List<RentalAsset>();

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

                await reservationQueueService.EnsureActiveTurnForBookingAsync(
                    customerId,
                    rental,
                    cancellationToken);

                if (rental.Type == RentalAssetType.Location && rental.QueueEnabled)
                {
                    queuedLocations.Add(rental);
                }

                var multiDayPricing = calendarDays.HasValue
                    ? await ResolveMultiDayPricingAsync(rental, request.Date, request.EndDate!.Value,
                        request.StartTime, request.EndTime, cancellationToken)
                    : null;

                if (!calendarDays.HasValue && rental.SchedulePolicy == SchedulePolicy.OpenHours
                    && OpenHoursRules.ValidateInterval(
                        rental, request.StartTime, request.EndTime, rental.Asset.Name) is { } openHoursError)
                {
                    throw new InvalidOperationException(openHoursError);
                }

                IReadOnlyList<(TimeOnly Start, TimeOnly End)>? slotGridWindows = null;
                if (rental.SchedulePolicy == SchedulePolicy.SlotGrid)
                {
                    var plan = await ResolveSlotGridWindowsAsync(
                        rental, request.Date, request.StartTime, request.EndTime, cancellationToken);
                    if (!plan.IsOpen)
                    {
                        throw new InvalidOperationException(
                            $"'{rental.Asset.Name}' is not open for the requested interval.");
                    }

                    slotGridWindows = plan.Windows;
                    // Shared Goods keep schedule cells open; reservations account for quantity.
                    // A Slot has only one ReservationId and cannot represent shared occupancy.
                    if (rental.Type == RentalAssetType.Location || rental.TotalQuantity == 1)
                        validatedSlotIds.AddRange(plan.PersistedSlotIds);
                }

                var reservedQuantity = await GetReservedQuantityAsync(
                    tenantId,
                    rental.Id,
                    start,
                    end,
                    excludeReservationId: null,
                    cancellationToken);

                var availableQuantity = rental.TotalQuantity - reservedQuantity;

                if (rental.Type == RentalAssetType.Location && reservedQuantity > 0)
                {
                    throw new InvalidOperationException(
                        ReservationBookingRules.LocationUnavailable(rental.Asset.Name));
                }

                if (rental.Type == RentalAssetType.Good && quantity > availableQuantity)
                {
                    throw new InvalidOperationException(
                        $"Insufficient quantity for '{rental.Asset.Name}'. Available: {availableQuantity}, requested: {quantity}.");
                }

                requiresDeposit |= rental.RequiresDeposit;

                decimal subTotal;
                decimal itemUnitPrice;
                if (slotGridWindows is { Count: > 1 } windows)
                {
                    var (total, _, unitPrice) = await ResolveSlotGridPricingAsync(
                        rental.Id, request.Date, request.StartTime, request.EndTime,
                        windows, hours, quantity, zone, cancellationToken);
                    subTotal = total;
                    itemUnitPrice = unitPrice;
                }
                else
                {
                    var pricing = multiDayPricing ?? await ResolvePricingAsync(
                        rental.Id,
                        request.Date,
                        request.StartTime,
                        request.EndTime,
                        hours,
                        quantity,
                        cancellationToken);
                    subTotal = BookingOffer.Total(pricing, hours, quantity, calendarDays);
                    itemUnitPrice = pricing.PricePerHour;
                }

                totalAmount += subTotal;

                var item = new ReservationItem
                {
                    TenantId = tenantId,
                    ReservationId = reservation.Id,
                    RentalAssetId = rental.Id,
                    Quantity = quantity,
                    UnitPrice = itemUnitPrice,
                    SubTotal = subTotal,
                };

                reservation.AddItem(item);
                item.RentalAsset = rental;
                itemResponses.Add((item, rental.AssetId, rental.Asset.Name));
            }

            reservation.TotalAmount = RoundMoney(totalAmount);
            if (request.ExpectedTotalAmount is decimal expected && expected != reservation.TotalAmount)
            {
                throw new PriceChangedException(reservation.TotalAmount);
            }

            reservation.OpenAccordingToPaymentPolicy(requiresDeposit);

            dbContext.Reservations.Add(reservation);

            var coveredSlots = await dbContext.Slots
                .Where(s => s.TenantId == tenantId && validatedSlotIds.Contains(s.Id))
                .ToListAsync(cancellationToken);
            foreach (var slot in coveredSlots)
            {
                slot.MarkBooked(reservation.Id);
            }

            foreach (var queued in queuedLocations)
            {
                await reservationQueueService.CompleteTurnAsync(
                    customerId,
                    queued,
                    reservation.Id,
                    cancellationToken);
            }

            var queuedDeliveries = await DispatchOpeningEventAsync(
                reservation,
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            var response = ToResponse(reservation, itemResponses,
                await policies.ReadTenantAsync(tenantId, cancellationToken),
                await policies.ReadCategoriesAsync(tenantId, cancellationToken));
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            ScheduleDeliveries(queuedDeliveries);

            return response;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

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

        var tenantPolicy = await policies.ReadTenantAsync(EnsureTenantContext(), cancellationToken);
        var categories = await policies.ReadCategoriesAsync(EnsureTenantContext(), cancellationToken);
        return reservations
            .Select(r => ToResponse(
                r,
                r.Items
                    .Select(i => (
                        i,
                        i.RentalAsset.AssetId,
                        i.RentalAsset.Asset.Name))
                    .ToList(), tenantPolicy, categories))
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
        var zone = await ResolveBookingZoneAsync(tenantProvider.TenantId!.Value, cancellationToken);

        var query = dbContext.Reservations
            .AsNoTracking()
            .Include(r => r.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(a => a.Asset)
            .AsQueryable();

        if (from is DateOnly fromDate)
        {
            var fromStart = TenantBookingClock.CivilDay(fromDate, zone).Start;
            query = query.Where(r => r.EndDateTime > fromStart);
        }

        if (to is DateOnly toDate)
        {
            var toEnd = TenantBookingClock.CivilDay(toDate, zone).EndExclusive;
            query = query.Where(r => r.StartDateTime < toEnd);
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

        var tenantPolicy = await policies.ReadTenantAsync(EnsureTenantContext(), cancellationToken);
        var categories = await policies.ReadCategoriesAsync(EnsureTenantContext(), cancellationToken);
        return reservations
            .Select(r => ToResponse(
                r,
                r.Items
                    .Select(i => (
                        i,
                        i.RentalAsset.AssetId,
                        i.RentalAsset.Asset.Name))
                    .ToList(), tenantPolicy, categories))
            .ToList();
    }

    public async Task<ReservationResponseDto> ConfirmAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await ReservationLocks.LockByReservationIdAsync(
                dbContext,
                tenantId,
                reservationId,
                cancellationToken);

            var reservation = await LoadReservationWithItemsAsync(reservationId, cancellationToken)
                ?? throw new KeyNotFoundException($"Reservation '{reservationId}' was not found.");

            if (reservation.Status is ReservationStatus.Canceled or ReservationStatus.Completed)
            {
                throw new InvalidOperationException(
                    $"Cannot confirm a reservation in status '{reservation.Status}'.");
            }

            if (reservation.Status == ReservationStatus.Confirmed)
            {
                var unchangedResponse = await ToResponseFromEntityAsync(reservation, cancellationToken);
                await CommitIfPresentAsync(transaction, cancellationToken);
                return unchangedResponse;
            }

            if (reservation.Status != ReservationStatus.PendingDeposit)
            {
                throw new InvalidOperationException(
                    $"Only pending reservations can be confirmed (current: '{reservation.Status}').");
            }

            reservation.Status = ReservationStatus.Confirmed;
            reservation.Touch();
            var queuedDeliveries = await domainEvents.DispatchAsync(
                new ReservationEvent(reservation, RentalEventTypes.ReservationConfirmed),
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            var response = await ToResponseFromEntityAsync(reservation, cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);
            ScheduleDeliveries(queuedDeliveries);

            return response;
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task<ReservationResponseDto> CompleteAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await ReservationLocks.LockByReservationIdAsync(
                dbContext,
                tenantId,
                reservationId,
                cancellationToken);

            var reservation = await LoadReservationWithItemsAsync(reservationId, cancellationToken)
                ?? throw new KeyNotFoundException($"Reservation '{reservationId}' was not found.");

            if (reservation.Status is ReservationStatus.PendingDeposit or ReservationStatus.Canceled)
            {
                throw new InvalidOperationException(
                    $"Cannot complete a reservation in status '{reservation.Status}'.");
            }

            if (reservation.Status == ReservationStatus.Completed)
            {
                var unchangedResponse = await ToResponseFromEntityAsync(reservation, cancellationToken);
                await CommitIfPresentAsync(transaction, cancellationToken);
                return unchangedResponse;
            }

            if (reservation.Status != ReservationStatus.Confirmed)
            {
                throw new InvalidOperationException(
                    $"Only confirmed reservations can be completed (current: '{reservation.Status}').");
            }

            reservation.Status = ReservationStatus.Completed;
            reservation.Touch();
            var queuedDeliveries = await domainEvents.DispatchAsync(
                new ReservationEvent(reservation, RentalEventTypes.ReservationCompleted),
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            var response = await ToResponseFromEntityAsync(reservation, cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);
            ScheduleDeliveries(queuedDeliveries);

            return response;
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task<ReservationResponseDto> CancelAsync(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await ReservationLocks.LockByReservationIdAsync(
                dbContext,
                tenantId,
                reservationId,
                cancellationToken);

            var reservation = await LoadReservationWithItemsAsync(reservationId, cancellationToken)
                ?? throw new KeyNotFoundException($"Reservation '{reservationId}' was not found.");

            if (reservation.Status == ReservationStatus.Canceled)
            {
                var unchangedResponse = await ToResponseFromEntityAsync(reservation, cancellationToken);
                await CommitIfPresentAsync(transaction, cancellationToken);
                return unchangedResponse;
            }

            if (reservation.Status == ReservationStatus.Completed)
            {
                throw new InvalidOperationException("Cannot cancel a completed reservation.");
            }

            await ReleaseOccupancyAsync(reservation, cancellationToken);
            var queuedDeliveries = await domainEvents.DispatchAsync(
                new ReservationEvent(reservation, RentalEventTypes.ReservationCanceled),
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            var response = await ToResponseFromEntityAsync(reservation, cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);
            ScheduleDeliveries(queuedDeliveries);

            return response;
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    public async Task<ReservationResponseDto> CancelByCustomerAsync(
        Guid customerId,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await ReservationLocks.LockByReservationIdAsync(
                dbContext,
                tenantId,
                reservationId,
                cancellationToken);

            var reservation = await LoadReservationWithItemsAsync(reservationId, cancellationToken);
            if (reservation is null || reservation.CustomerId != customerId)
            {
                throw new KeyNotFoundException("Reservation not found.");
            }

            if (reservation.Status == ReservationStatus.Canceled)
            {
                var unchangedResponse = await ToResponseFromEntityAsync(reservation, cancellationToken);
                await CommitIfPresentAsync(transaction, cancellationToken);
                return unchangedResponse;
            }

            if (reservation.Status == ReservationStatus.Completed)
            {
                throw new InvalidOperationException("Cannot cancel a completed reservation.");
            }

            var tenantPolicy = await policies.ReadTenantAsync(tenantId, cancellationToken);
            var categories = await policies.ReadCategoriesAsync(tenantId, cancellationToken);
            var cutoff = PolicyResolver.StrictestCancelCutoffHours(
                reservation.Items.Select(item => item.RentalAsset), categories, tenantPolicy);
            var now = timeProvider.GetUtcNow();
            if (cutoff is > 0 && now >= reservation.StartDateTime.AddHours(-cutoff.Value))
            {
                throw new CancelCutoffPassedException(cutoff.Value);
            }

            if (now >= reservation.StartDateTime)
            {
                throw new InvalidOperationException(
                    "Cannot cancel a reservation that has already started.");
            }

            await ReleaseOccupancyAsync(reservation, cancellationToken);
            var queuedDeliveries = await domainEvents.DispatchAsync(
                new ReservationEvent(reservation, RentalEventTypes.ReservationCanceled),
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            var response = await ToResponseFromEntityAsync(reservation, cancellationToken);
            await CommitIfPresentAsync(transaction, cancellationToken);
            ScheduleDeliveries(queuedDeliveries);

            return response;
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

    private async Task ReleaseOccupancyAsync(
        Reservation reservation,
        CancellationToken cancellationToken)
    {
        var rentalAssetIds = reservation.Items
            .Select(item => item.RentalAssetId)
            .Distinct()
            .ToList();

        await RentalAssetLocks.LockByRentalAssetIdsAsync(
            dbContext,
            reservation.TenantId,
            rentalAssetIds,
            cancellationToken);

        reservation.Status = ReservationStatus.Canceled;
        reservation.Touch();

        var linkedSlots = await dbContext.Slots
            .Where(s => s.ReservationId == reservation.Id)
            .ToListAsync(cancellationToken);

        foreach (var slot in linkedSlots)
        {
            slot.MarkAvailable();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ReservationResponseDto> ToResponseFromEntityAsync(
        Reservation reservation, CancellationToken cancellationToken) =>
        ToResponse(
            reservation,
            reservation.Items
                .Select(i => (
                    i,
                    i.RentalAsset.AssetId,
                    i.RentalAsset.Asset.Name))
                .ToList(),
            await policies.ReadTenantAsync(reservation.TenantId, cancellationToken),
            await policies.ReadCategoriesAsync(reservation.TenantId, cancellationToken));

    private Task<Reservation?> LoadReservationWithItemsAsync(
        Guid reservationId,
        CancellationToken cancellationToken) =>
        dbContext.Reservations
            .Include(r => r.Customer)
            .Include(r => r.Items)
                .ThenInclude(i => i.RentalAsset)
                    .ThenInclude(a => a.Asset)
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

    private async Task<IReadOnlyList<Guid>> DispatchOpeningEventAsync(
        Reservation reservation,
        CancellationToken cancellationToken)
    {
        var eventType = reservation.Status == ReservationStatus.PendingDeposit
            ? RentalEventTypes.ReservationPendingDeposit
            : RentalEventTypes.ReservationConfirmed;
        return await domainEvents.DispatchAsync(
            new ReservationEvent(reservation, eventType),
            cancellationToken);
    }

    private void ScheduleDeliveries(IReadOnlyList<Guid> deliveryIds)
    {
        foreach (var deliveryId in deliveryIds)
        {
            outboxScheduler.Schedule(deliveryId);
        }
    }

    private static async Task CommitIfPresentAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task RollbackIfPresentAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    /// <summary>
    /// A SlotGrid interval is open iff it is exactly the union of one or more contiguous,
    /// open windows (persisted slots take precedence over template winners by StartTime).
    /// Bookings spanning more than one window additionally require the total duration to be
    /// one of the rental's AllowedDurationMinutes; when that is unset, only a single window
    /// may be booked (today's behavior).
    /// </summary>
    private async Task<SlotGridWindowPlan> ResolveSlotGridWindowsAsync(
        RentalAsset rental,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var persistedSlots = await dbContext.Slots
            .AsNoTracking()
            .Include(s => s.OccupancyKind)
            .Where(s => s.TenantId == tenantId && s.RentalAssetId == rental.Id && s.Date == date)
            .ToListAsync(cancellationToken);

        var templates = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.OccupancyKind)
            .Where(t => t.TenantId == tenantId
                        && t.RentalAssetId == rental.Id
                        && t.IsActive
                        && t.DayOfWeek == date.DayOfWeek)
            .ToListAsync(cancellationToken);
        var templateWindows = OccupancyPrecedence.SplitWinningWindows(templates);

        var normalizedEnd = TenantBookingClock.NormalizeEndTime(endTime);
        var windows = new List<(TimeOnly Start, TimeOnly End)>();
        var persistedSlotIds = new List<Guid>();
        if (persistedSlots.Any(s => s.StartTime < normalizedEnd
            && TenantBookingClock.NormalizeEndTime(s.EndTime) > startTime
            && (s.Status != SlotStatus.Available || !s.OccupancyKind.IsActive
                || !s.OccupancyKind.IsBookableByCustomer)))
        {
            return new SlotGridWindowPlan(false, windows, persistedSlotIds);
        }

        var cursor = startTime;

        while (cursor < normalizedEnd)
        {
            bool open;
            TimeOnly windowEnd;

            var persisted = persistedSlots.FirstOrDefault(s => s.StartTime == cursor);
            if (persisted is not null)
            {
                windowEnd = TenantBookingClock.NormalizeEndTime(persisted.EndTime);
                open = persisted.Status == SlotStatus.Available
                       && persisted.OccupancyKind.IsActive
                       && persisted.OccupancyKind.IsBookableByCustomer;
            }
            else
            {
                var winner = templateWindows.FirstOrDefault(w => w.Start == cursor);
                if (winner.Template is null)
                {
                    return new SlotGridWindowPlan(false, windows, persistedSlotIds);
                }

                windowEnd = TenantBookingClock.NormalizeEndTime(winner.End);
                open = winner.Template.OccupancyKind.IsActive
                       && winner.Template.OccupancyKind.IsBookableByCustomer;
            }

            if (!open || windowEnd <= cursor)
            {
                return new SlotGridWindowPlan(false, windows, persistedSlotIds);
            }

            if (persisted is not null)
                persistedSlotIds.Add(persisted.Id);
            windows.Add((cursor, windowEnd));
            cursor = windowEnd;
        }

        if (cursor != normalizedEnd)
        {
            return new SlotGridWindowPlan(false, windows, persistedSlotIds);
        }

        if (windows.Count > 1)
        {
            if (string.IsNullOrWhiteSpace(rental.AllowedDurationMinutes))
            {
                return new SlotGridWindowPlan(false, windows, persistedSlotIds);
            }

            var totalMinutes = (TenantBookingClock.EndOfDayTime(normalizedEnd) - startTime.ToTimeSpan()).TotalMinutes;
            if (!OpenHoursRules.ParseDurations(rental.AllowedDurationMinutes).Any(minutes => minutes == totalMinutes))
            {
                return new SlotGridWindowPlan(false, windows, persistedSlotIds);
            }
        }

        return new SlotGridWindowPlan(true, windows, persistedSlotIds);
    }

    private readonly record struct SlotGridWindowPlan(
        bool IsOpen,
        IReadOnlyList<(TimeOnly Start, TimeOnly End)> Windows,
        IReadOnlyList<Guid> PersistedSlotIds);

    /// <summary>
    /// Slot prices apply to each window; hourly bands use each window's elapsed time.
    /// Day and Fixed offers cover the whole interval (Fixed once per item).
    /// </summary>
    private async Task<(decimal Total, PricingUnit Unit, decimal UnitPrice)> ResolveSlotGridPricingAsync(
        Guid rentalAssetId,
        DateOnly date,
        TimeOnly overallStart,
        TimeOnly overallEnd,
        IReadOnlyList<(TimeOnly Start, TimeOnly End)> windows,
        decimal overallHours,
        int quantity,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        var prices = await dbContext.RentalPricings.AsNoTracking()
            .Where(p => p.RentalAssetId == rentalAssetId && p.DayOfWeek == date.DayOfWeek)
            .ToListAsync(cancellationToken);
        var fullRangePricing = BookingOffer.ForDerivedWindow(
            prices.Where(p => p.PricingUnit != PricingUnit.Slot
                && p.StartTime <= overallStart
                && p.EndTime >= TenantBookingClock.NormalizeEndTime(overallEnd)),
            overallHours, quantity);

        decimal? windowTotal = 0m;
        RentalPricing? lastWindowPrice = null;
        foreach (var window in windows)
        {
            var windowHours = (decimal)(TenantBookingClock.EndAtLocal(date, window.End, zone)
                - TenantBookingClock.AtLocal(date, window.Start, zone)).TotalHours;
            var pricing = BookingOffer.ForDerivedWindow(
                prices.Where(p => p.PricingUnit == PricingUnit.Slot
                    && p.StartTime <= window.Start
                    && p.EndTime >= TenantBookingClock.NormalizeEndTime(window.End)),
                windowHours, quantity);
            decimal? cellTotal = pricing is null ? null : BookingOffer.Total(pricing, windowHours, quantity);

            var hourlyPrices = prices.Where(p => p.PricingUnit == PricingUnit.Hour).ToList();
            var boundaries = hourlyPrices.SelectMany(p => new[] { p.StartTime, p.EndTime })
                .Where(t => t > window.Start && t < window.End)
                .Append(window.Start).Append(window.End).Distinct().Order().ToList();
            decimal? hourlyTotal = 0m;
            RentalPricing? lastHourlyPrice = null;
            for (var i = 1; i < boundaries.Count; i++)
            {
                var segmentStart = boundaries[i - 1];
                var segmentEnd = boundaries[i];
                var segmentHours = (decimal)(TenantBookingClock.EndAtLocal(date, segmentEnd, zone)
                    - TenantBookingClock.AtLocal(date, segmentStart, zone)).TotalHours;
                var hourlyPrice = BookingOffer.ForDerivedWindow(
                    hourlyPrices.Where(p => p.StartTime <= segmentStart && p.EndTime >= segmentEnd),
                    segmentHours, quantity);
                if (hourlyPrice is null)
                {
                    hourlyTotal = null;
                    break;
                }
                // Keep hourly fragments unrounded until the complete item total is known.
                hourlyTotal += hourlyPrice.PricePerHour * segmentHours * quantity;
                lastHourlyPrice = hourlyPrice;
            }

            if (hourlyTotal is not null && (cellTotal is null || hourlyTotal < cellTotal))
            {
                cellTotal = hourlyTotal;
                pricing = lastHourlyPrice;
            }
            if (cellTotal is null)
            {
                windowTotal = null;
                break;
            }

            windowTotal += cellTotal;
            lastWindowPrice = pricing;
        }

        if (fullRangePricing is not null)
        {
            var total = BookingOffer.Total(fullRangePricing, overallHours, quantity);
            if (windowTotal is null || total <= windowTotal.Value)
            {
                return (total, fullRangePricing.PricingUnit, fullRangePricing.PricePerHour);
            }
        }

        if (windowTotal is not null && lastWindowPrice is not null)
        {
            return (RoundMoney(windowTotal.Value), lastWindowPrice.PricingUnit, lastWindowPrice.PricePerHour);
        }

        throw new InvalidOperationException(
            "No rental pricing covers the requested day and time window.");
    }

    internal async Task<int> GetReservedQuantityAsync(
        Guid tenantId,
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
            where item.TenantId == tenantId
                  && reservation.TenantId == tenantId
                  && item.RentalAssetId == rentalAssetId
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

    private async Task<RentalPricing> ResolvePricingAsync(
        Guid rentalAssetId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        decimal hours,
        int quantity,
        CancellationToken cancellationToken)
    {
        var dayOfWeek = date.DayOfWeek;

        endTime = TenantBookingClock.NormalizeEndTime(endTime);
        var prices = await dbContext.RentalPricings
            .AsNoTracking()
            .Where(p => p.RentalAssetId == rentalAssetId && p.DayOfWeek == dayOfWeek)
            .Where(p => p.StartTime <= startTime && p.EndTime >= endTime)
            .OrderBy(p => p.PricePerHour)
            .ToListAsync(cancellationToken);
        var pricing = BookingOffer.ForDerivedWindow(prices, hours, quantity);

        if (pricing is null)
        {
            throw new InvalidOperationException(
                "No rental pricing covers the requested day and time window.");
        }

        return pricing;
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

    private sealed class MultiDayUnavailableException(string message, Exception? innerException = null)
        : ArgumentException(message, innerException);

    private async Task<RentalPricing> ResolveMultiDayPricingAsync(
        RentalAsset rental, DateOnly date, DateOnly endDate, TimeOnly startTime, TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        if (rental.SchedulePolicy == SchedulePolicy.SlotGrid)
            throw new ArgumentException("Multi-day booking is not supported for SlotGrid resources.");

        var slots = await dbContext.Slots.AsNoTracking().Include(s => s.OccupancyKind)
            .Where(s => s.TenantId == rental.TenantId && s.RentalAssetId == rental.Id
                && s.Date >= date && s.Date <= endDate)
            .ToListAsync(cancellationToken);
        RentalPricing? firstPricing = null;
        for (var dayNumber = date.DayNumber; dayNumber <= endDate.DayNumber; dayNumber++)
        {
            var day = DateOnly.FromDayNumber(dayNumber);
            var dayStart = day == date ? startTime : rental.OpenTime ?? TimeOnly.MinValue;
            var dayEnd = day == endDate ? endTime : rental.CloseTime ?? TimeOnly.MinValue;
            if (rental.SchedulePolicy == SchedulePolicy.OpenHours
                && OpenHoursRules.ValidateInterval(rental, dayStart, dayEnd, rental.Asset.Name) is { } error)
                throw new MultiDayUnavailableException(error);
            if (slots.Any(s => s.Date == day
                && OccupancyPrecedence.IntervalsOverlap(s.StartTime, s.EndTime, dayStart, dayEnd)
                && (s.Status == SlotStatus.Cancelled || s.OccupancyKind is not { IsActive: true }
                    || s.OccupancyKind.TenantId != rental.TenantId
                    || (s.OccupancyKind.BlocksCapacity && !s.OccupancyKind.IsBookableByCustomer))))
                throw new MultiDayUnavailableException($"'{rental.Asset.Name}' is not open for the requested interval.");

            RentalPricing pricing;
            try
            {
                pricing = await ResolvePricingAsync(rental.Id, day, dayStart, dayEnd,
                    (decimal)(TenantBookingClock.EndOfDayTime(dayEnd) - dayStart.ToTimeSpan()).TotalHours,
                    1, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                throw new MultiDayUnavailableException(ex.Message, ex);
            }
            if (pricing.PricingUnit != PricingUnit.Day)
                throw new ArgumentException("Multi-day booking requires a per-day resource.");
            firstPricing ??= pricing;
        }

        return firstPricing!;
    }

    private static void ValidateTimeRange(DateOnly date, TimeOnly startTime, TimeOnly endTime, DateOnly? endDate)
    {
        if ((endDate ?? date) < date)
            throw new ArgumentException("EndDate must not be before Date.");
        if ((endDate ?? date).DayNumber - date.DayNumber >= 31)
            throw new ArgumentException("Reservations may span at most 31 civil days.");
        if ((endDate ?? date) == date && endTime <= startTime)
        {
            throw new ArgumentException("EndTime must be greater than StartTime.");
        }
    }

    private Task<TimeZoneInfo> ResolveBookingZoneAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        TenantBookingClock.ResolveForTenantAsync(dbContext, tenantId, cancellationToken);

    private static (DateTimeOffset Start, DateTimeOffset End) ToDateTimeRange(
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        TimeZoneInfo zone,
        DateOnly? endDate)
    {
        var start = TenantBookingClock.AtLocal(date, startTime, zone);
        var end = TenantBookingClock.EndAtLocal(endDate ?? date, endTime, zone);
        if (end <= start)
        {
            throw new ArgumentException("EndTime must be greater than StartTime.");
        }

        return (start, end);
    }

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static ReservationResponseDto ToResponse(
        Reservation reservation,
        IReadOnlyList<(ReservationItem Item, Guid AssetId, string AssetName)> items,
        BookingPolicy? tenantPolicy, IReadOnlyDictionary<Guid, BookingPolicy> categories) =>
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
                x.Item.SubTotal)).ToList(),
            PolicyResolver.CancelDeadlineUtc(reservation.StartDateTime,
                items.Select(x => x.Item.RentalAsset), categories, tenantPolicy));
}
