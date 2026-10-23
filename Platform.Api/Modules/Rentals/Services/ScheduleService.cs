using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Notifications;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;
// Write paths share the read model's projections (ToSlotDto, ResolvePersistedSource, TrimLabel, ...).
using static Platform.Api.Modules.Rentals.Services.AvailabilityReadModel;
// Shared write guards (ValidateTimeRange, CommitIfPresentAsync, EnsureNoTemplateCollision, ...).
using static Platform.Api.Modules.Rentals.Services.ScheduleWriteGuards;
// Template grid key shared with the weekly rule (TemplateExactKey).
using static Platform.Api.Modules.Rentals.Services.ScheduleTemplateWriter;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class ScheduleService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService,
    ITrialGuard trialGuard,
    IReservationQueueService reservationQueueService,
    IRentalsNotificationPublisher notificationPublisher,
    INotificationOutboxScheduler outboxScheduler,
    TimeProvider timeProvider) : IScheduleService
{
    private readonly AvailabilityReadModel availability =
        new(dbContext, tenantProvider, occupancyKindService, timeProvider);

    private readonly ScheduleTemplateWriter templates =
        new(dbContext, tenantProvider, occupancyKindService);

    private readonly DayPublisher days =
        new(dbContext, tenantProvider, occupancyKindService);

    private readonly ScheduleWriteGuards guards = new(dbContext, tenantProvider);

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

    public async Task<ApplyWeeklyRuleResponseDto> ApplyWeeklyRuleAsync(
        ApplyWeeklyRuleRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        if (request.RentalAssetIds is null || request.RentalAssetIds.Count == 0)
        {
            throw new ArgumentException("At least one rentable is required.");
        }

        if (request.DaysOfWeek is null || request.DaysOfWeek.Count == 0)
        {
            throw new ArgumentException("At least one weekday is required.");
        }

        ValidateTimeRange(request.OpenTime, request.CloseTime);
        var slotMinutes = request.SlotMinutes <= 0 ? 60 : request.SlotMinutes;
        if (request.OpenTime.AddMinutes(slotMinutes) > request.CloseTime)
        {
            throw new ArgumentException("SlotMinutes must fit within the open interval.");
        }

        var rentableIds = request.RentalAssetIds.Where(id => id != Guid.Empty).Distinct().ToList();
        await guards.EnsureRentablesAsync(tenantId, rentableIds, cancellationToken);
        await guards.EnsureOccupancyKindAsync(tenantId, request.OccupancyKindId, cancellationToken);
        var label = TrimLabel(request.Label);

        await using var transaction = await guards.BeginAssetLockedTransactionAsync(
            tenantId, rentableIds, cancellationToken);
        try
        {
        var existing = await dbContext.ScheduleTemplates
            .Where(t => t.TenantId == tenantId
                        && rentableIds.Contains(t.RentalAssetId)
                        && request.DaysOfWeek.Contains(t.DayOfWeek))
            .ToListAsync(cancellationToken);

        var byExact = existing
            .GroupBy(t => new TemplateExactKey(
                t.RentalAssetId, t.DayOfWeek, t.StartTime, t.EndTime, t.OccupancyKindId))
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(t => t.Id).First());

        var created = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var rentableId in rentableIds)
        {
            foreach (var dayOfWeek in request.DaysOfWeek.Distinct())
            {
                var cursor = request.OpenTime;
                while (true)
                {
                    var end = cursor.AddMinutes(slotMinutes);
                    if (end > request.CloseTime || end <= cursor)
                    {
                        break;
                    }

                    var key = new TemplateExactKey(
                        rentableId, dayOfWeek, cursor, end, request.OccupancyKindId);
                    if (byExact.ContainsKey(key))
                    {
                        skipped++;
                        cursor = end;
                        continue;
                    }

                    EnsureNoTemplateCollision(
                        existing,
                        rentableId,
                        dayOfWeek,
                        cursor,
                        end,
                        request.OccupancyKindId,
                        excludeId: null);

                    var entity = new ScheduleTemplate
                    {
                        TenantId = tenantId,
                        RentalAssetId = rentableId,
                        DayOfWeek = dayOfWeek,
                        StartTime = cursor,
                        EndTime = end,
                        OccupancyKindId = request.OccupancyKindId,
                        Label = label,
                        IsActive = request.IsActive,
                    };
                    dbContext.ScheduleTemplates.Add(entity);
                    existing.Add(entity);
                    byExact[key] = entity;
                    created++;

                    cursor = end;
                }
            }
        }

        var policyChanged = await templates.EnsureSlotGridPolicyAsync(tenantId, rentableIds, cancellationToken);

        if (created > 0 || updated > 0 || policyChanged)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await CommitIfPresentAsync(transaction, cancellationToken);
        return new ApplyWeeklyRuleResponseDto(created, updated, skipped);
        }
        catch
        {
            await RollbackIfPresentAsync(transaction, cancellationToken);
            throw;
        }
    }

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
        await notificationPublisher.EnsureReadyAsync(cancellationToken);
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

            var unitPrice = await ResolveHourlyPriceAsync(
                slot.RentalAssetId,
                slot.Date,
                slot.StartTime,
                slot.EndTime,
                cancellationToken);

            var subTotal = BookingOffer.Total(unitPrice, hours, quantity);
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
                UnitPrice = unitPrice,
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
            var queuedDeliveries = await notificationPublisher.PublishReservationEventAsync(
                reservation,
                eventType,
                cancellationToken);

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
                ]);
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

    private async Task<decimal> ResolveHourlyPriceAsync(
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

        return pricing.PricePerHour;
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
