using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class ExpectedPriceTests
{
    [Theory]
    [InlineData(false, 99)] [InlineData(true, 99)]
    [InlineData(false, 101)] [InlineData(true, 101)]
    [InlineData(false, -1)] [InlineData(true, -1)]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 100.001)] [InlineData(true, 100.001)]
    public async Task Mismatch_returns_current_total_without_reservation_slot_change_or_turn_consumption(
        bool persisted, decimal expected)
    {
        await using var h = await ReservationQueueHarness.CreateAsync(includeSlot: true);
        var active = await ActivateAsync(h);

        var error = await Assert.ThrowsAsync<PriceChangedException>(() => BookAsync(h, persisted, expected));

        Assert.Equal(100m, error.CurrentTotalAmount);
        // A later SaveChanges must not accidentally flush a rejected reservation.
        await h.Db.SaveChangesAsync();
        Assert.Empty(await h.Db.Reservations.ToListAsync());
        Assert.Empty(await h.Db.ReservationItems.ToListAsync());
        var slot = await h.Db.Slots.SingleAsync();
        Assert.Equal(SlotStatus.Available, slot.Status);
        Assert.Null(slot.ReservationId);
        var unchanged = await h.Queue().GetStatusAsync(h.RentalAssetId, h.CustomerA, CancellationToken.None);
        Assert.Equal(QueueTicketStatus.Active, unchanged.MyTicket!.Status);
        Assert.Equal(active.MyTicket!.TurnExpiresAt, unchanged.MyTicket.TurnExpiresAt);
        Assert.Equal(QueueTicketStatus.Waiting,
            (await h.Queue().GetStatusAsync(h.RentalAssetId, h.CustomerB, CancellationToken.None)).MyTicket!.Status);

        // An explicit corrected retry succeeds once and completes the original turn.
        var created = await BookAsync(h, persisted, error.CurrentTotalAmount);
        Assert.Equal(100m, created.TotalAmount);
        var completed = await h.Db.ReservationQueueTickets.SingleAsync(t => t.CustomerId == h.CustomerA);
        Assert.Equal(QueueTicketStatus.Completed, completed.Status);
        Assert.Equal(created.Id, completed.CompletedReservationId);
        Assert.Single(await h.Db.Reservations.ToListAsync());
    }

    [Theory]
    [InlineData(false, null)] [InlineData(true, null)]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 100)] [InlineData(true, 100)]
    public async Task Null_preserves_legacy_and_zero_and_matching_prices_are_accepted(bool persisted, int? quotedAmount)
    {
        decimal? expected = quotedAmount;
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, includeSlot: true);
        var price = await h.Db.RentalPricings.SingleAsync();
        price.PricePerHour = expected ?? 123.45m;
        await h.Db.SaveChangesAsync();
        var created = await BookAsync(h, persisted, expected);
        Assert.Equal(expected ?? 123.45m, created.TotalAmount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Daily_quote_rounding_duration_and_stock_quantity_match_submission(bool persisted)
    {
        await using var h = await ReservationQueueHarness.CreateAsync(
            queueEnabled: false, includeSlot: true, type: RentalAssetType.Good);
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.TotalQuantity = 5;
        rental.AllowedDurationMinutes = "30";
        (await h.Db.RentalPricings.SingleAsync()).PricePerHour = 20.01m;
        (await h.Db.Slots.SingleAsync()).EndTime = new TimeOnly(10, 30);
        if (!persisted) h.Db.Slots.RemoveRange(await h.Db.Slots.ToListAsync());
        await h.Db.SaveChangesAsync();

        var quote = await new DailyAvailabilityService(h.Db, h.TenantProvider, h.Time).GetAsync(
            ReservationQueueHarness.BookDate, [h.RentalAssetId], h.UnitId, 3, 30, CancellationToken.None);
        var window = Assert.Single(Assert.Single(quote.Resources).Windows,
            w => w.StartTime == ReservationQueueHarness.BookStart);
        Assert.Equal(30.02m, window.TotalAmount);
        Assert.Equal("BRL", quote.Currency);
        Assert.Equal(TimeSpan.FromSeconds(60), quote.QuoteExpiresAt - quote.ServerNow);
        var created = persisted
            ? await h.Schedule().BookSlotAsync(h.CustomerA, new BookSlotRequestDto
            {
                UnitId = h.UnitId, SlotId = h.SlotId!.Value, Quantity = 3, ExpectedTotalAmount = window.TotalAmount
            }, CancellationToken.None)
            : await h.Reservations().CreateReservationAsync(h.CustomerA, h.BookRequest() with
            {
                EndTime = new TimeOnly(10, 30), ExpectedTotalAmount = window.TotalAmount,
                Items = [new() { AssetId = h.AssetId, Quantity = 3 }]
            }, CancellationToken.None);
        Assert.Equal(window.TotalAmount, created.TotalAmount);
    }

    [Fact]
    public async Task Multi_item_guard_compares_whole_reservation_and_rejection_does_not_consume_any_turn()
    {
        await using var h = await ReservationQueueHarness.CreateAsync();
        var asset = await h.Db.Assets.SingleAsync();
        var second = new Asset
        {
            TenantId = h.TenantId, UnitId = h.UnitId, CategoryId = asset.CategoryId,
            FamilyId = asset.FamilyId, Name = "Second resource", Tag = "Second", IsRentable = true,
            Status = AssetStatus.Active
        };
        var rental = new RentalAsset
        {
            TenantId = h.TenantId, AssetId = second.Id, Type = RentalAssetType.Good,
            TotalQuantity = 4, IsActive = true, RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours
        };
        h.Db.AddRange(second, rental, new RentalPricing
        {
            TenantId = h.TenantId, RentalAssetId = rental.Id, DayOfWeek = ReservationQueueHarness.BookDate.DayOfWeek,
            StartTime = new(8, 0), EndTime = new(22, 0), PricePerHour = 25m,
            RequiresDeposit = false, DepositPercentage = 0
        });
        await h.Db.SaveChangesAsync();
        await ActivateAsync(h);
        var request = h.BookRequest() with
        {
            ExpectedTotalAmount = 100m,
            Items = [new() { AssetId = h.AssetId }, new() { AssetId = second.Id, Quantity = 2 }]
        };
        var error = await Assert.ThrowsAsync<PriceChangedException>(() =>
            h.Reservations().CreateReservationAsync(h.CustomerA, request, CancellationToken.None));
        Assert.Equal(150m, error.CurrentTotalAmount);
        Assert.Empty(await h.Db.Reservations.ToListAsync());
        Assert.Equal(QueueTicketStatus.Active,
            (await h.Queue().GetStatusAsync(h.RentalAssetId, h.CustomerA, CancellationToken.None)).MyTicket!.Status);
        var created = await h.Reservations().CreateReservationAsync(h.CustomerA,
            request with { ExpectedTotalAmount = error.CurrentTotalAmount }, CancellationToken.None);
        Assert.Equal(150m, created.TotalAmount);
        Assert.Equal(2, created.Items.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_price_remains_a_validation_error_even_if_expected_is_zero(bool persisted)
    {
        await using var h = await ReservationQueueHarness.CreateAsync(includeSlot: true);
        await ActivateAsync(h);
        h.Db.RentalPricings.RemoveRange(await h.Db.RentalPricings.ToListAsync());
        await h.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BookAsync(h, persisted, 0));
        Assert.Contains("pricing", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await h.Db.Reservations.ToListAsync());
        Assert.Equal(QueueTicketStatus.Active,
            (await h.Queue().GetStatusAsync(h.RentalAssetId, h.CustomerA, CancellationToken.None)).MyTicket!.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Mismatch_does_not_bypass_queue_admission(bool persisted)
    {
        await using var h = await ReservationQueueHarness.CreateAsync(includeSlot: true);
        h.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BookAsync(h, persisted, 99));
        Assert.Equal(ReservationQueueCodes.Required, error.Message);
        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Foreign_tenant_pricing_cannot_change_the_comparison(bool persisted)
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, includeSlot: true);
        h.Db.RentalPricings.Add(new RentalPricing
        {
            TenantId = Guid.NewGuid(), RentalAssetId = h.RentalAssetId,
            DayOfWeek = ReservationQueueHarness.BookDate.DayOfWeek,
            StartTime = new(10, 0), EndTime = new(11, 0), PricePerHour = 1,
            RequiresDeposit = false, DepositPercentage = 0
        });
        await h.Db.SaveChangesAsync();
        Assert.Equal(100m, (await BookAsync(h, persisted, 100m)).TotalAmount);
    }

    internal static async Task<ReservationQueueStatusDto> ActivateAsync(ReservationQueueHarness h)
    {
        await h.Queue().JoinAsync(h.RentalAssetId, h.CustomerA, CancellationToken.None);
        await h.Queue().JoinAsync(h.RentalAssetId, h.CustomerB, CancellationToken.None);
        h.Time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var active = await h.Queue().GetStatusAsync(h.RentalAssetId, h.CustomerA, CancellationToken.None);
        Assert.Equal(QueueTicketStatus.Active, active.MyTicket!.Status);
        return active;
    }

    internal static Task<ReservationResponseDto> BookAsync(ReservationQueueHarness h, bool persisted, decimal? expected) =>
        persisted
            ? h.Schedule().BookSlotAsync(h.CustomerA, new BookSlotRequestDto
            {
                UnitId = h.UnitId, SlotId = h.SlotId!.Value, ExpectedTotalAmount = expected
            }, CancellationToken.None)
            : h.Reservations().CreateReservationAsync(h.CustomerA,
                h.BookRequest() with { ExpectedTotalAmount = expected }, CancellationToken.None);
}
