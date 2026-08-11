using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class ScheduleService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IOccupancyKindService occupancyKindService) : IScheduleService
{
    private static readonly ReservationStatus[] BlockingStatuses =
    [
        ReservationStatus.PendingDeposit,
        ReservationStatus.Confirmed
    ];

    public async Task<IReadOnlyList<ScheduleTemplateResponseDto>> ListTemplatesAsync(
        Guid? rentalAssetId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();

        var query = dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.RentalAsset).ThenInclude(r => r.Asset)
            .Include(t => t.OccupancyKind)
            .AsQueryable();

        if (rentalAssetId is not null)
        {
            query = query.Where(t => t.RentalAssetId == rentalAssetId);
        }

        var items = await query
            .OrderBy(t => t.RentalAsset.Asset.Name)
            .ThenBy(t => t.DayOfWeek)
            .ThenBy(t => t.StartTime)
            .ToListAsync(cancellationToken);

        return items.Select(ToTemplateDto).ToList();
    }

    public async Task<ScheduleTemplateResponseDto> CreateTemplateAsync(
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);
        await EnsureRentableAsync(request.RentalAssetId, cancellationToken);
        await EnsureOccupancyKindAsync(request.OccupancyKindId, cancellationToken);

        var entity = new ScheduleTemplate
        {
            TenantId = tenantId,
            RentalAssetId = request.RentalAssetId,
            DayOfWeek = request.DayOfWeek,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            OccupancyKindId = request.OccupancyKindId,
            Label = TrimLabel(request.Label),
            IsActive = request.IsActive,
        };

        dbContext.ScheduleTemplates.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetTemplateDtoAsync(entity.Id, cancellationToken);
    }

    public async Task<ScheduleTemplateResponseDto> UpdateTemplateAsync(
        Guid id,
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);

        var entity = await dbContext.ScheduleTemplates
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Schedule template was not found.");

        await EnsureRentableAsync(request.RentalAssetId, cancellationToken);
        await EnsureOccupancyKindAsync(request.OccupancyKindId, cancellationToken);

        entity.RentalAssetId = request.RentalAssetId;
        entity.DayOfWeek = request.DayOfWeek;
        entity.StartTime = request.StartTime;
        entity.EndTime = request.EndTime;
        entity.OccupancyKindId = request.OccupancyKindId;
        entity.Label = TrimLabel(request.Label);
        entity.IsActive = request.IsActive;
        entity.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetTemplateDtoAsync(entity.Id, cancellationToken);
    }

    public async Task DeleteTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var entity = await dbContext.ScheduleTemplates
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Schedule template was not found.");

        dbContext.ScheduleTemplates.Remove(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SeedDefaultTemplatesResponseDto> SeedDefaultTemplatesAsync(
        SeedDefaultTemplatesRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);
        await EnsureRentableAsync(request.RentalAssetId, cancellationToken);

        var open = request.OpenTime ?? new TimeOnly(8, 0);
        var close = request.CloseTime ?? new TimeOnly(22, 0);
        var slotMinutes = request.SlotMinutes <= 0 ? 60 : request.SlotMinutes;

        if (close <= open)
        {
            throw new ArgumentException("CloseTime must be after OpenTime.");
        }

        if (open.AddMinutes(slotMinutes) > close)
        {
            throw new ArgumentException("SlotMinutes must fit within the open interval.");
        }

        OccupancyKind openKind;
        if (request.OccupancyKindId is { } kindId)
        {
            openKind = await dbContext.OccupancyKinds
                .FirstOrDefaultAsync(k => k.Id == kindId && k.IsActive, cancellationToken)
                ?? throw new KeyNotFoundException("Occupancy kind was not found.");
        }
        else
        {
            openKind = await dbContext.OccupancyKinds
                .FirstOrDefaultAsync(
                    k => k.Key == "open" && k.IsActive && k.IsBookableByCustomer,
                    cancellationToken)
                ?? await dbContext.OccupancyKinds
                    .FirstOrDefaultAsync(
                        k => k.IsActive && k.IsBookableByCustomer,
                        cancellationToken)
                ?? throw new InvalidOperationException(
                    "No bookable occupancy kind is available for the default grid.");
        }

        var existing = await dbContext.ScheduleTemplates
            .Where(t => t.RentalAssetId == request.RentalAssetId)
            .Select(t => new { t.DayOfWeek, t.StartTime, t.EndTime })
            .ToListAsync(cancellationToken);

        var existingKeys = existing
            .Select(row => $"{row.DayOfWeek}|{row.StartTime}|{row.EndTime}")
            .ToHashSet();

        var created = 0;
        var skipped = 0;

        foreach (DayOfWeek dayOfWeek in Enum.GetValues<DayOfWeek>())
        {
            var cursor = open;
            while (true)
            {
                var end = cursor.AddMinutes(slotMinutes);
                if (end > close)
                {
                    break;
                }

                var key = $"{dayOfWeek}|{cursor}|{end}";
                if (existingKeys.Contains(key))
                {
                    skipped++;
                    cursor = end;
                    continue;
                }

                dbContext.ScheduleTemplates.Add(new ScheduleTemplate
                {
                    TenantId = tenantId,
                    RentalAssetId = request.RentalAssetId,
                    DayOfWeek = dayOfWeek,
                    StartTime = cursor,
                    EndTime = end,
                    OccupancyKindId = openKind.Id,
                    Label = null,
                    IsActive = true,
                });
                existingKeys.Add(key);
                created++;
                cursor = end;
            }
        }

        if (created > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new SeedDefaultTemplatesResponseDto(created, skipped);
    }

    public async Task<DayScheduleResponseDto> GetDayAsync(
        DateOnly date,
        Guid? rentalAssetId,
        bool customerFacing,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var persisted = await LoadPersistedSlotsAsync(date, rentalAssetId, cancellationToken);
        var derived = await DeriveOpenHoursSlotsAsync(date, rentalAssetId, cancellationToken);

        IEnumerable<SlotResponseDto> all = persisted.Concat(derived);

        if (customerFacing)
        {
            all = all.Where(s =>
                s.Status == SlotStatus.Available && s.IsBookableByCustomer);
        }

        var slots = all
            .OrderBy(s => s.AssetName)
            .ThenBy(s => s.StartTime)
            .ToList();

        return new DayScheduleResponseDto(date, slots);
    }

    public async Task<int> PublishDayAsync(
        PublishDayRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await occupancyKindService.EnsureDefaultsAsync(cancellationToken);

        var dayOfWeek = request.Date.DayOfWeek;

        var templatesQuery = dbContext.ScheduleTemplates
            .Include(t => t.RentalAsset)
            .Where(t => t.IsActive && t.DayOfWeek == dayOfWeek);

        if (request.RentalAssetId is not null)
        {
            templatesQuery = templatesQuery.Where(t => t.RentalAssetId == request.RentalAssetId);
        }
        else
        {
            templatesQuery = templatesQuery.Where(t =>
                t.RentalAsset.IsActive
                && t.RentalAsset.SchedulePolicy == SchedulePolicy.SlotGrid);
        }

        var templates = await templatesQuery.ToListAsync(cancellationToken);
        var created = 0;

        foreach (var template in templates)
        {
            var exists = await dbContext.Slots.AnyAsync(
                s => s.RentalAssetId == template.RentalAssetId
                     && s.Date == request.Date
                     && s.StartTime == template.StartTime,
                cancellationToken);

            if (exists)
            {
                continue;
            }

            dbContext.Slots.Add(new Slot
            {
                TenantId = tenantId,
                RentalAssetId = template.RentalAssetId,
                Date = request.Date,
                StartTime = template.StartTime,
                EndTime = template.EndTime,
                OccupancyKindId = template.OccupancyKindId,
                Label = template.Label,
                Status = SlotStatus.Available,
                SourceTemplateId = template.Id,
            });
            created++;
        }

        if (created > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    public async Task<SlotResponseDto> UpsertSlotAsync(
        UpsertSlotRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateTimeRange(request.StartTime, request.EndTime);
        await EnsureRentableAsync(request.RentalAssetId, cancellationToken);
        await EnsureOccupancyKindAsync(request.OccupancyKindId, cancellationToken);

        var existing = await dbContext.Slots
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Include(s => s.OccupancyKind)
            .FirstOrDefaultAsync(
                s => s.RentalAssetId == request.RentalAssetId
                     && s.Date == request.Date
                     && s.StartTime == request.StartTime,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.Status == SlotStatus.Booked)
            {
                throw new InvalidOperationException("Cannot edit a booked slot.");
            }

            existing.EndTime = request.EndTime;
            existing.OccupancyKindId = request.OccupancyKindId;
            existing.Label = TrimLabel(request.Label);
            existing.Status = SlotStatus.Available;
            existing.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);

            await dbContext.Entry(existing).Reference(s => s.OccupancyKind).LoadAsync(cancellationToken);
            return ToSlotDto(existing, isDerived: false);
        }

        var slot = new Slot
        {
            TenantId = tenantId,
            RentalAssetId = request.RentalAssetId,
            Date = request.Date,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            OccupancyKindId = request.OccupancyKindId,
            Label = TrimLabel(request.Label),
            Status = SlotStatus.Available,
        };

        dbContext.Slots.Add(slot);
        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetSlotDtoAsync(slot.Id, cancellationToken);
    }

    public async Task CancelSlotAsync(Guid slotId, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var slot = await dbContext.Slots
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken)
            ?? throw new KeyNotFoundException("Slot was not found.");

        if (slot.Status == SlotStatus.Booked)
        {
            throw new InvalidOperationException("Cannot cancel a booked slot; cancel the reservation instead.");
        }

        slot.MarkCancelled();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ReservationResponseDto> BookSlotAsync(
        Guid customerId,
        BookSlotRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var quantity = request.Quantity < 1 ? 1 : request.Quantity;

        var customer = await dbContext.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Authenticated customer was not found.");

        if (customer.TenantId != tenantId)
        {
            throw new UnauthorizedAccessException("Customer does not belong to the current tenant.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var slot = await dbContext.Slots
                .Include(s => s.OccupancyKind)
                .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
                .FirstOrDefaultAsync(s => s.Id == request.SlotId, cancellationToken)
                ?? throw new KeyNotFoundException("Slot was not found.");

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

            var start = ToDateTime(slot.Date, slot.StartTime);
            var end = ToDateTime(slot.Date, slot.EndTime);
            var hours = (decimal)(end - start).TotalHours;

            var reservedQuantity = await GetReservedQuantityAsync(
                slot.RentalAssetId, start, end, cancellationToken);

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

            var subTotal = RoundMoney(unitPrice * hours * quantity);
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

            dbContext.Reservations.Add(reservation);
            slot.MarkBooked(reservation.Id);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

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
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<List<SlotResponseDto>> LoadPersistedSlotsAsync(
        DateOnly date,
        Guid? rentalAssetId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Slots
            .AsNoTracking()
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Include(s => s.OccupancyKind)
            .Where(s => s.Date == date && s.Status != SlotStatus.Cancelled);

        if (rentalAssetId is not null)
        {
            query = query.Where(s => s.RentalAssetId == rentalAssetId);
        }

        var slots = await query.ToListAsync(cancellationToken);
        return slots.Select(s => ToSlotDto(s, isDerived: false)).ToList();
    }

    private async Task<List<SlotResponseDto>> DeriveOpenHoursSlotsAsync(
        DateOnly date,
        Guid? rentalAssetId,
        CancellationToken cancellationToken)
    {
        var rentablesQuery = dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
            .Where(r => r.IsActive
                        && r.SchedulePolicy == SchedulePolicy.OpenHours
                        && r.OpenTime != null
                        && r.CloseTime != null);

        if (rentalAssetId is not null)
        {
            rentablesQuery = rentablesQuery.Where(r => r.Id == rentalAssetId);
        }

        var rentables = await rentablesQuery.ToListAsync(cancellationToken);
        if (rentables.Count == 0)
        {
            return [];
        }

        var openKind = await dbContext.OccupancyKinds
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == "open" && k.IsActive, cancellationToken);

        if (openKind is null)
        {
            return [];
        }

        var derived = new List<SlotResponseDto>();

        foreach (var rental in rentables)
        {
            var open = rental.OpenTime!.Value;
            var close = rental.CloseTime!.Value;
            if (close <= open)
            {
                continue;
            }

            var durations = ParseDurations(rental.AllowedDurationMinutes);
            foreach (var minutes in durations)
            {
                var cursor = open;
                while (true)
                {
                    var end = cursor.AddMinutes(minutes);
                    if (end > close)
                    {
                        break;
                    }

                    var startDt = ToDateTime(date, cursor);
                    var endDt = ToDateTime(date, end);
                    var reserved = await GetReservedQuantityAsync(
                        rental.Id, startDt, endDt, cancellationToken);

                    var available = rental.Type == RentalAssetType.Location
                        ? reserved == 0
                        : reserved < rental.TotalQuantity;

                    if (available)
                    {
                        // Skip if a persisted slot already covers this start
                        var covered = await dbContext.Slots.AsNoTracking().AnyAsync(
                            s => s.RentalAssetId == rental.Id
                                 && s.Date == date
                                 && s.StartTime == cursor
                                 && s.Status != SlotStatus.Cancelled,
                            cancellationToken);

                        if (!covered)
                        {
                            derived.Add(new SlotResponseDto(
                                Guid.Empty,
                                rental.Id,
                                rental.Asset.Name,
                                date,
                                cursor,
                                end,
                                openKind.Id,
                                openKind.Key,
                                openKind.Label,
                                openKind.ColorHex,
                                openKind.IsBookableByCustomer,
                                null,
                                SlotStatus.Available,
                                null,
                                IsDerived: true));
                        }
                    }

                    cursor = end;
                }
            }
        }

        return derived;
    }

    private static IReadOnlyList<int> ParseDurations(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [60];
        }

        var list = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var m) ? m : 0)
            .Where(m => m > 0)
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        return list.Count == 0 ? [60] : list;
    }

    private async Task<int> GetReservedQuantityAsync(
        Guid rentalAssetId,
        DateTimeOffset start,
        DateTimeOffset end,
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
        var pricing = await dbContext.RentalPricings
            .AsNoTracking()
            .Where(p => p.RentalAssetId == rentalAssetId
                        && p.DayOfWeek == dayOfWeek
                        && p.StartTime <= startTime
                        && p.EndTime >= endTime)
            .OrderByDescending(p => p.StartTime)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "No pricing covers the requested interval for this rentable.");

        return pricing.PricePerHour;
    }

    private async Task EnsureRentableAsync(Guid rentalAssetId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.RentalAssets
            .AnyAsync(r => r.Id == rentalAssetId && r.IsActive, cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Rentable was not found.");
        }
    }

    private async Task EnsureOccupancyKindAsync(Guid occupancyKindId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.OccupancyKinds
            .AnyAsync(k => k.Id == occupancyKindId && k.IsActive, cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Occupancy kind was not found.");
        }
    }

    private async Task<ScheduleTemplateResponseDto> GetTemplateDtoAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.ScheduleTemplates
            .AsNoTracking()
            .Include(t => t.RentalAsset).ThenInclude(r => r.Asset)
            .Include(t => t.OccupancyKind)
            .FirstAsync(t => t.Id == id, cancellationToken);
        return ToTemplateDto(entity);
    }

    private async Task<SlotResponseDto> GetSlotDtoAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.Slots
            .AsNoTracking()
            .Include(s => s.RentalAsset).ThenInclude(r => r.Asset)
            .Include(s => s.OccupancyKind)
            .FirstAsync(s => s.Id == id, cancellationToken);
        return ToSlotDto(entity, isDerived: false);
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static void ValidateTimeRange(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
        {
            throw new ArgumentException("End time must be after start time.");
        }
    }

    private static string? TrimLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private static DateTimeOffset ToDateTime(DateOnly date, TimeOnly time) =>
        new(date.ToDateTime(time), TimeSpan.Zero);

    private static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static ScheduleTemplateResponseDto ToTemplateDto(ScheduleTemplate t) =>
        new(
            t.Id,
            t.RentalAssetId,
            t.RentalAsset.Asset.Name,
            t.DayOfWeek,
            t.StartTime,
            t.EndTime,
            t.OccupancyKindId,
            t.OccupancyKind.Label,
            t.Label,
            t.IsActive);

    private static SlotResponseDto ToSlotDto(Slot s, bool isDerived) =>
        new(
            s.Id,
            s.RentalAssetId,
            s.RentalAsset.Asset.Name,
            s.Date,
            s.StartTime,
            s.EndTime,
            s.OccupancyKindId,
            s.OccupancyKind.Key,
            s.OccupancyKind.Label,
            s.OccupancyKind.ColorHex,
            s.OccupancyKind.IsBookableByCustomer,
            s.Label,
            s.Status,
            s.ReservationId,
            isDerived);
}
