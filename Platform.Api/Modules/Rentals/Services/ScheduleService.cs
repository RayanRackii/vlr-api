using Microsoft.EntityFrameworkCore;
using Platform.Api.DomainEvents;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Events;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;
// Read-model helpers used by ListTemplates/BookSlot (ResolveRentableIds, BlockingStatuses).
using static Platform.Api.Modules.Rentals.Services.AvailabilityReadModel;
// Template projection used by ListTemplates (ToTemplateDto).
using static Platform.Api.Modules.Rentals.Services.ScheduleTemplateWriter;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class ScheduleService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService,
    ITrialGuard trialGuard,
    IReservationQueueService reservationQueueService,
    IDomainEventDispatcher<ReservationEvent> domainEvents,
    INotificationOutboxScheduler outboxScheduler,
    TimeProvider timeProvider) : IScheduleService
{
    private readonly AvailabilityReadModel availability =
        new(dbContext, tenantProvider, occupancyKindService, timeProvider);

    private readonly ScheduleTemplateWriter templates =
        new(dbContext, tenantProvider, occupancyKindService);

    private readonly DayPublisher days =
        new(dbContext, tenantProvider, occupancyKindService);

    private readonly WeeklyRuleApplier weeklyRules =
        new(dbContext, tenantProvider, occupancyKindService);

    public async Task<IReadOnlyList<ScheduleTemplateResponseDto>> ListTemplatesAsync(
        Guid? rentalAssetId,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? rentalAssetIds = null,
        DayOfWeek? dayOfWeek = null)
    {
        EnsureTenant();

        var ids = ResolveRentableIds(rentalAssetId, rentalAssetIds);

        var query = dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.RentalAsset).ThenInclude(r => r.Asset)
            .Include(t => t.OccupancyKind)
            .AsQueryable();

        if (ids.Count > 0)
        {
            query = query.Where(t => ids.Contains(t.RentalAssetId));
        }

        if (dayOfWeek is { } day)
        {
            query = query.Where(t => t.DayOfWeek == day);
        }

        var items = await query
            .OrderBy(t => t.RentalAsset.Asset.Name)
            .ThenBy(t => t.DayOfWeek)
            .ThenBy(t => t.StartTime)
            .ToListAsync(cancellationToken);

        return items.Select(ToTemplateDto).ToList();
    }

    public Task<ScheduleTemplateResponseDto> CreateTemplateAsync(
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken) =>
        templates.CreateTemplateAsync(request, cancellationToken);

    public Task<ScheduleTemplateResponseDto> UpdateTemplateAsync(
        Guid id,
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken) =>
        templates.UpdateTemplateAsync(id, request, cancellationToken);

    public Task DeleteTemplateAsync(Guid id, CancellationToken cancellationToken) =>
        templates.DeleteTemplateAsync(id, cancellationToken);

    public Task<SeedDefaultTemplatesResponseDto> SeedDefaultTemplatesAsync(
        SeedDefaultTemplatesRequestDto request,
        CancellationToken cancellationToken) =>
        templates.SeedDefaultTemplatesAsync(request, cancellationToken);

    public Task<ApplyWeeklyRuleResponseDto> ApplyWeeklyRuleAsync(
        ApplyWeeklyRuleRequestDto request,
        CancellationToken cancellationToken) =>
        weeklyRules.ApplyWeeklyRuleAsync(request, cancellationToken);

    public Task<DayScheduleResponseDto> GetDayAsync(
        DateOnly date,
        Guid? rentalAssetId,
        bool customerFacing,
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? rentalAssetIds = null) =>
        availability.GetDayAsync(date, rentalAssetId, customerFacing, cancellationToken, rentalAssetIds);

    public Task<int> PublishDayAsync(
        PublishDayRequestDto request,
        CancellationToken cancellationToken) =>
        days.PublishDayAsync(request, cancellationToken);

    public Task<SlotResponseDto> UpsertSlotAsync(
        UpsertSlotRequestDto request,
        CancellationToken cancellationToken) =>
        days.UpsertSlotAsync(request, cancellationToken);

    public Task CancelSlotAsync(Guid slotId, CancellationToken cancellationToken) =>
        days.CancelSlotAsync(slotId, cancellationToken);

    public Task<SlotResponseDto> ApplyDailyOccurrenceAsync(
        ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken) =>
        days.ApplyDailyOccurrenceAsync(request, cancellationToken);

    public async Task<ReservationResponseDto> BookSlotAsync(
        Guid customerId,
        BookSlotRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await trialGuard.EnsureWritableAsync(cancellationToken);
        await domainEvents.EnsureReadyAsync(cancellationToken);
        var quantity = request.Quantity < 1 ? 1 : request.Quantity;

        var customer = await dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Authenticated customer was not found.");

        if (customer.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("Customer does not belong to the current tenant.");
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var slotKey = await dbContext.Slots
                .Where(s => s.Id == request.SlotId && s.TenantId == tenantId)
                .Select(s => new { s.RentalAssetId, s.Date, s.StartTime })
                .FirstOrDefaultAsync(cancellationToken);

            if (slotKey is null)
            {
                throw new KeyNotFoundException("Slot was not found.");
            }

            var zone = await TenantBookingClock.ResolveForTenantAsync(
                dbContext, tenantId, cancellationToken);
            ReservationBookingRules.EnsureStartNotBeforeNow(
                TenantBookingClock.AtLocal(slotKey.Date, slotKey.StartTime, zone),
                timeProvider);

            await BookingHorizon.EnsureRentalAssetAsync(
                dbContext,
                tenantId,
                slotKey.RentalAssetId,
                slotKey.Date,
                timeProvider,
                cancellationToken);

            await BookingPolicyEnforcement.EnsureCustomerCanBookAsync(
                dbContext,
                tenantId,
                customer.Id,
                slotKey.Date,
                cancellationToken);

            await RentalAssetLocks.LockByRentalAssetIdAsync(
                dbContext,
                tenantId,
                slotKey.RentalAssetId,
                cancellationToken);

            await BookingHorizon.EnsureRentalAssetAsync(
                dbContext,
                tenantId,
                slotKey.RentalAssetId,
                slotKey.Date,
                timeProvider,
                cancellationToken);

            var slot = await dbContext.Slots
                .Include(s => s.OccupancyKind)
                .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
                .FirstOrDefaultAsync(
                    s => s.Id == request.SlotId && s.TenantId == tenantId,
                    cancellationToken)
                ?? throw new KeyNotFoundException("Slot was not found.");

            var start = TenantBookingClock.AtLocal(slot.Date, slot.StartTime, zone);
            ReservationBookingRules.EnsureStartNotBeforeNow(start, timeProvider);

            await reservationQueueService.EnsureActiveTurnForBookingAsync(
                customerId,
                slot.RentalAsset,
                cancellationToken);

            if (slot.Status != SlotStatus.Available
                || !slot.OccupancyKind.IsBookableByCustomer
                || !slot.OccupancyKind.IsActive)
            {
                throw new InvalidOperationException("Slot is not available for booking.");
            }

            if (slot.RentalAsset.Asset.UnitId != request.UnitId)
            {
                throw new InvalidOperationException("Slot does not belong to the given unit.");
            }

            var end = TenantBookingClock.AtLocal(slot.Date, slot.EndTime, zone);
            if (end <= start)
            {
                throw new ArgumentException("End time must be after start time.");
            }

            if (slot.RentalAsset.SchedulePolicy == SchedulePolicy.OpenHours
                && OpenHoursRules.ValidateInterval(slot.RentalAsset, slot.StartTime, slot.EndTime,
                    slot.RentalAsset.Asset.Name) is { } intervalError)
            {
                throw new InvalidOperationException(intervalError);
            }

            var hours = (decimal)(end - start).TotalHours;

            var reservedQuantity = await GetReservedQuantityAsync(
                tenantId, slot.RentalAssetId, start, end, cancellationToken);

            if (slot.RentalAsset.Type == RentalAssetType.Location && reservedQuantity > 0)
            {
                throw new InvalidOperationException("Location is already reserved for this interval.");
            }

            if (slot.RentalAsset.Type == RentalAssetType.Good
                && quantity > slot.RentalAsset.TotalQuantity - reservedQuantity)
            {
                throw new InvalidOperationException("Insufficient quantity for this slot.");
            }

            var pricing = await ResolvePricingAsync(
                slot.RentalAssetId,
                slot.Date,
                slot.StartTime,
                slot.EndTime,
                cancellationToken);

            var subTotal = BookingOffer.Total(pricing, hours, quantity);
            if (request.ExpectedTotalAmount is decimal expected && expected != subTotal)
            {
                throw new PriceChangedException(subTotal);
            }

            var whatsApp = string.IsNullOrWhiteSpace(customer.Phone) ? string.Empty : customer.Phone;

            var reservation = new Reservation
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                CustomerId = customer.Id,
                CustomerName = customer.Name,
                CustomerWhatsApp = whatsApp,
                StartDateTime = start,
                EndDateTime = end,
                Status = ReservationStatus.PendingDeposit,
                TotalAmount = subTotal,
                DepositPaid = 0m,
            };

            var item = new ReservationItem
            {
                TenantId = tenantId,
                ReservationId = reservation.Id,
                RentalAssetId = slot.RentalAssetId,
                Quantity = quantity,
                UnitPrice = pricing.PricePerHour,
                SubTotal = subTotal,
            };
            reservation.AddItem(item);
            item.RentalAsset = slot.RentalAsset;

            dbContext.Reservations.Add(reservation);
            reservation.OpenAccordingToPaymentPolicy(slot.RentalAsset.RequiresDeposit);
            slot.MarkBooked(reservation.Id);

            await reservationQueueService.CompleteTurnAsync(
                customerId,
                slot.RentalAsset,
                reservation.Id,
                cancellationToken);

            var eventType = reservation.Status == ReservationStatus.PendingDeposit
                ? RentalEventTypes.ReservationPendingDeposit
                : RentalEventTypes.ReservationConfirmed;
            var queuedDeliveries = await domainEvents.DispatchAsync(
                new ReservationEvent(reservation, eventType),
                cancellationToken);

            var tenantPolicy = await new PolicyResolver(dbContext).ReadTenantAsync(tenantId, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            foreach (var deliveryId in queuedDeliveries)
            {
                outboxScheduler.Schedule(deliveryId);
            }

            return new ReservationResponseDto(
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
                [
                    new ReservationItemResponseDto(
                        item.Id,
                        slot.RentalAsset.AssetId,
                        slot.RentalAssetId,
                        slot.RentalAsset.Asset.Name,
                        item.Quantity,
                        item.UnitPrice,
                        item.SubTotal)
                ],
                PolicyResolver.CancelDeadlineUtc(reservation.StartDateTime, [slot.RentalAsset],
                    tenantPolicy));
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

    internal async Task<int> GetReservedQuantityAsync(
        Guid tenantId,
        Guid rentalAssetId,
        DateTimeOffset start,
        DateTimeOffset end,
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
            select item.Quantity;

        return await query.SumAsync(cancellationToken);
    }

    private async Task<RentalPricing> ResolvePricingAsync(
        Guid rentalAssetId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken)
    {
        var dayOfWeek = date.DayOfWeek;
        var pricing = await BookingOffer.ForPersistedSlot(dbContext.RentalPricings
            .AsNoTracking()
            .Where(p => p.RentalAssetId == rentalAssetId
                        && p.DayOfWeek == dayOfWeek
                        && p.StartTime <= startTime
                        && p.EndTime >= endTime))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "No pricing covers the requested interval for this rentable.");

        return pricing;
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
