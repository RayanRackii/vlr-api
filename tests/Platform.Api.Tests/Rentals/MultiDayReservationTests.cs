using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class MultiDayReservationTests
{
    private static readonly DateOnly Date = LocationBookingHarness.Date.AddDays(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Three_civil_days_quote_and_create_charge_three_days_for_two_items()
    {
        await using var h = await CreateHarnessAsync();
        var service = h.CreateReservationService();
        var quote = await service.CheckAvailabilityAsync(Quote(h, Date.AddDays(2)), CancellationToken.None);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(600m, quote.EstimatedTotalAmount);
        var created = await service.CreateReservationAsync(h.CustomerId,
            Create(h, Date.AddDays(2)) with { ExpectedTotalAmount = 600m }, CancellationToken.None);
        Assert.Equal(600m, created.TotalAmount);
        Assert.Equal(600m, Assert.Single(created.Items).SubTotal);
        Assert.Equal(TimeSpan.FromHours(58), created.EndDateTime - created.StartDateTime);
    }

    [Theory]
    [InlineData(PricingUnit.Hour)]
    [InlineData(PricingUnit.Slot)]
    [InlineData(PricingUnit.Fixed)]
    public async Task Multi_day_non_Day_pricing_returns_400_in_quote_and_create(PricingUnit unit)
    {
        await using var h = await CreateHarnessAsync();
        foreach (var pricing in await h.Db.RentalPricings.ToListAsync())
            pricing.PricingUnit = unit;
        await h.Db.SaveChangesAsync();
        await AssertBadRequestAsync(h, Quote(h, Date.AddDays(2)), Create(h, Date.AddDays(2)),
            "Multi-day booking requires a per-day resource.");
    }

    private static ReservationsController Controller(LocationBookingHarness h) =>
        new(h.CreateReservationService(), null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(CustomerClaimTypes.CustomerId, h.CustomerId.ToString())], "test")),
                },
            },
        };

    [Theory]
    [InlineData(-1, "EndDate must not be before Date.")]
    [InlineData(31, "Reservations may span at most 31 civil days.")]
    public async Task Invalid_date_span_returns_400_in_quote_and_create(int offset, string message)
    {
        await using var h = await CreateHarnessAsync();
        await AssertBadRequestAsync(h, Quote(h, Date.AddDays(offset)), Create(h, Date.AddDays(offset)), message);
    }

    [Fact]
    public async Task Multi_day_SlotGrid_returns_400_in_quote_and_create()
    {
        await using var h = await CreateHarnessAsync();
        (await h.Db.RentalAssets.SingleAsync()).SchedulePolicy = SchedulePolicy.SlotGrid;
        await h.Db.SaveChangesAsync();
        await AssertBadRequestAsync(h, Quote(h, Date.AddDays(2)), Create(h, Date.AddDays(2)),
            "Multi-day booking is not supported for SlotGrid resources.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Horizon_checks_last_day_using_tenant_or_resource_limit(bool resourceOverride)
    {
        await using var h = await CreateHarnessAsync();
        h.Db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = h.TenantId, MaxAdvanceBookingDays = resourceOverride ? 10 : 2,
        });
        if (resourceOverride) (await h.Db.RentalAssets.SingleAsync()).MaxAdvanceBookingDays = 2;
        await h.Db.SaveChangesAsync();
        await AssertBadRequestAsync(h, Quote(h, Date.AddDays(2)), Create(h, Date.AddDays(2)),
            new BookingHorizonExceededException().Message, BookingHorizonExceededException.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closed_or_cancelled_middle_day_returns_400(bool cancelled)
    {
        await using var h = await CreateHarnessAsync();
        var kind = new OccupancyKind
        {
            TenantId = h.TenantId, Key = cancelled ? "open" : "closed", Label = "Middle day",
            IsActive = true, IsBookableByCustomer = cancelled, BlocksCapacity = true, SortOrder = 0,
        };
        h.Db.OccupancyKinds.Add(kind);
        h.Db.Slots.Add(new Slot
        {
            TenantId = h.TenantId, RentalAssetId = h.RentalAssetId, Date = Date.AddDays(1),
            StartTime = new(8, 0), EndTime = new(18, 0), OccupancyKindId = kind.Id,
            Status = cancelled ? SlotStatus.Cancelled : SlotStatus.Available,
        });
        await h.Db.SaveChangesAsync();
        await AssertBadRequestAsync(h, Quote(h, Date.AddDays(2)), Create(h, Date.AddDays(2)),
            $"'{LocationBookingHarness.AssetName}' is not open for the requested interval.");
    }

    [Fact]
    public async Task Unpriced_middle_day_returns_400_without_creating_a_reservation()
    {
        await using var h = await CreateHarnessAsync();
        h.Db.RentalPricings.Remove(await h.Db.RentalPricings.SingleAsync(
            p => p.DayOfWeek == Date.AddDays(1).DayOfWeek));
        await h.Db.SaveChangesAsync();
        await AssertBadRequestAsync(h, Quote(h, Date.AddDays(2)), Create(h, Date.AddDays(2)),
            "No rental pricing covers the requested day and time window.");
    }

    [Theory]
    [InlineData(8, 20, 60)]
    [InlineData(12, 12, 48)]
    [InlineData(16, 10, 42)]
    public async Task Day_prices_use_inclusive_civil_days_even_with_equal_or_earlier_end_time(
        int startHour, int endHour, int elapsedHours)
    {
        await using var h = await CreateHarnessAsync();
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.CloseTime = new(22, 0);
        rental.AllowedDurationMinutes = "120,240,360,600,720,840";
        foreach (var price in await h.Db.RentalPricings.ToListAsync()) price.EndTime = new(22, 0);
        await h.Db.SaveChangesAsync();
        var quote = Quote(h, Date.AddDays(2)) with { StartTime = new(startHour, 0), EndTime = new(endHour, 0) };
        var create = Create(h, Date.AddDays(2)) with
        {
            StartTime = quote.StartTime, EndTime = quote.EndTime, ExpectedTotalAmount = 600m,
        };
        var available = await h.CreateReservationService().CheckAvailabilityAsync(quote, CancellationToken.None);
        Assert.True(available.IsAvailable, available.Reason);
        Assert.Equal(600m, available.EstimatedTotalAmount);
        var created = await h.CreateReservationService().CreateReservationAsync(h.CustomerId, create, CancellationToken.None);
        Assert.Equal(600m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromHours(elapsedHours), created.EndDateTime - created.StartDateTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Omitted_null_or_same_EndDate_preserves_single_day_booking(bool sameDate)
    {
        await using var h = await CreateHarnessAsync();
        // Legacy payloads omit endDate entirely; null is also covered by Quote/Create helpers.
        var quote = JsonSerializer.Deserialize<CheckAvailabilityRequestDto>(JsonSerializer.Serialize(new
        {
            assetId = h.AssetId, date = Date, startTime = new TimeOnly(8, 0),
            endTime = new TimeOnly(18, 0), quantity = 2,
        }, Json), Json)!;
        Assert.Null(quote.EndDate);
        if (sameDate) quote = quote with { EndDate = Date };
        var available = await h.CreateReservationService().CheckAvailabilityAsync(quote, CancellationToken.None);
        Assert.True(available.IsAvailable, available.Reason);
        Assert.Equal(200m, available.EstimatedTotalAmount);
        var created = await h.CreateReservationService().CreateReservationAsync(h.CustomerId,
            Create(h, sameDate ? Date : null), CancellationToken.None);
        Assert.Equal(200m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromHours(10), created.EndDateTime - created.StartDateTime);
    }

    [Fact]
    public async Task Exactly_31_civil_days_are_accepted()
    {
        await using var h = await CreateHarnessAsync();
        var quote = await h.CreateReservationService().CheckAvailabilityAsync(Quote(h, Date.AddDays(30)), CancellationToken.None);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(6200m, quote.EstimatedTotalAmount);
        var created = await h.CreateReservationService().CreateReservationAsync(h.CustomerId,
            Create(h, Date.AddDays(30)) with { ExpectedTotalAmount = 6200m }, CancellationToken.None);
        Assert.Equal(6200m, created.TotalAmount);
    }

    [Theory]
    [InlineData(ReservationStatus.PendingDeposit)]
    [InlineData(ReservationStatus.Confirmed)]
    public async Task Existing_day_two_reservation_reduces_quantity_and_conflicts_with_create(ReservationStatus status)
    {
        await using var h = await CreateHarnessAsync();
        h.SeedOverlappingReservation(status,
            BrazilTimeZone.AtLocal(Date.AddDays(1), new(10, 0)),
            BrazilTimeZone.AtLocal(Date.AddDays(1), new(11, 0)));
        await h.Db.SaveChangesAsync();
        var available = await h.CreateReservationService().CheckAvailabilityAsync(Quote(h, Date.AddDays(2)), CancellationToken.None);
        Assert.False(available.IsAvailable);
        Assert.Equal(1, available.AvailableQuantity);
        var created = await Controller(h).Create(Create(h, Date.AddDays(2)), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(created.Result);
        Assert.Single(await h.CreateReservationService().ListMineAsync(h.CustomerId, CancellationToken.None));
    }

    [Fact]
    public async Task Daily_customer_limit_counts_only_the_start_date()
    {
        await using var h = await CreateHarnessAsync();
        (await h.Db.RentalAssets.SingleAsync()).TotalQuantity = 4;
        h.Db.BookingPolicies.Add(new BookingPolicy { TenantId = h.TenantId, MaxActiveReservationsPerCustomerPerDay = 1 });
        await h.Db.SaveChangesAsync();
        var created = await Controller(h).Create(Create(h, Date.AddDays(2)), CancellationToken.None);
        Assert.IsType<CreatedResult>(created.Result);
        var sameDay = await Controller(h).Create(Create(h, null), CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(sameDay.Result);
        Assert.Equal("DAILY_BOOKING_LIMIT_REACHED", JsonSerializer.SerializeToElement(conflict.Value, Json).GetProperty("code").GetString());
        var nextDay = await Controller(h).Create(Create(h, null) with { Date = Date.AddDays(1) }, CancellationToken.None);
        Assert.IsType<CreatedResult>(nextDay.Result);
    }

    [Fact]
    public async Task Every_item_must_have_Day_pricing_and_failed_cart_creates_nothing()
    {
        await using var h = await CreateHarnessAsync();
        var otherId = h.SeedGoodRentalAsset(2);
        var other = await h.Db.RentalAssets.FindAsync(otherId);
        other!.CloseTime = new(18, 0);
        other.AllowedDurationMinutes = "600";
        h.Db.RentalPricings.Add(new RentalPricing
        {
            TenantId = h.TenantId, RentalAssetId = otherId, DayOfWeek = Date.DayOfWeek,
            StartTime = new(8, 0), EndTime = new(18, 0), PricingUnit = PricingUnit.Hour,
            PricePerHour = 100m, RequiresDeposit = false, DepositPercentage = 0m,
        });
        await h.Db.SaveChangesAsync();
        var create = Create(h, Date.AddDays(2));
        await AssertBadRequestAsync(h, Quote(h, Date.AddDays(2)) with { AssetId = other.AssetId },
            create with { Items = [.. create.Items, new() { AssetId = other.AssetId }] },
            "Multi-day booking requires a per-day resource.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Middle_and_final_day_pricing_must_also_be_per_day(int offset)
    {
        await using var h = await CreateHarnessAsync();
        (await h.Db.RentalPricings.SingleAsync(p => p.DayOfWeek == Date.AddDays(offset).DayOfWeek))
            .PricingUnit = PricingUnit.Hour;
        await h.Db.SaveChangesAsync();
        await AssertBadRequestAsync(h, Quote(h, Date.AddDays(2)), Create(h, Date.AddDays(2)),
            "Multi-day booking requires a per-day resource.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reservations_touching_range_endpoints_do_not_conflict(bool touchesEnd)
    {
        await using var h = await CreateHarnessAsync();
        var boundary = touchesEnd
            ? BrazilTimeZone.AtLocal(Date.AddDays(2), new(18, 0))
            : BrazilTimeZone.AtLocal(Date, new(8, 0));
        h.SeedOverlappingReservation(ReservationStatus.Confirmed,
            touchesEnd ? boundary : boundary.AddHours(-1),
            touchesEnd ? boundary.AddHours(1) : boundary, quantity: 2);
        await h.Db.SaveChangesAsync();
        var quote = await h.CreateReservationService().CheckAvailabilityAsync(Quote(h, Date.AddDays(2)), CancellationToken.None);
        Assert.True(quote.IsAvailable, quote.Reason);
        Assert.Equal(2, quote.AvailableQuantity);
        var created = await h.CreateReservationService().CreateReservationAsync(h.CustomerId,
            Create(h, Date.AddDays(2)), CancellationToken.None);
        Assert.Equal(600m, created.TotalAmount);
    }

    [Theory]
    [InlineData(2026, 10, 31, 49, 16, 17)]
    [InlineData(2027, 3, 13, 47, 17, 16)]
    public async Task Tenant_timezone_DST_preserves_civil_endpoints_and_three_day_price(
        int year, int month, int day, int elapsedHours, int utcStartHour, int utcEndHour)
    {
        await using var h = await CreateHarnessAsync();
        h.Db.TenantSettings.Add(new TenantSetting
        {
            TenantId = h.TenantId, Key = Platform.Core.Domain.Constants.TenantSettingKeys.BookingTimeZoneId,
            Value = "America/New_York",
        });
        (await h.Db.RentalAssets.SingleAsync()).AllowedDurationMinutes = "240,360,600";
        await h.Db.SaveChangesAsync();
        var date = new DateOnly(year, month, day);
        var endDate = date.AddDays(2);
        var quote = Quote(h, endDate) with { Date = date, StartTime = new(12, 0), EndTime = new(12, 0) };
        var available = await h.CreateReservationService().CheckAvailabilityAsync(quote, CancellationToken.None);
        Assert.True(available.IsAvailable, available.Reason);
        Assert.Equal(600m, available.EstimatedTotalAmount);
        var created = await h.CreateReservationService().CreateReservationAsync(h.CustomerId,
            Create(h, endDate) with { Date = date, StartTime = quote.StartTime, EndTime = quote.EndTime }, CancellationToken.None);
        Assert.Equal(600m, created.TotalAmount);
        Assert.Equal(TimeSpan.FromHours(elapsedHours), created.EndDateTime - created.StartDateTime);
        Assert.Equal(new DateTimeOffset(year, month, day, utcStartHour, 0, 0, TimeSpan.Zero), created.StartDateTime);
        Assert.Equal(new DateTimeOffset(endDate.Year, endDate.Month, endDate.Day, utcEndHour, 0, 0, TimeSpan.Zero), created.EndDateTime);
    }

    private static async Task AssertBadRequestAsync(LocationBookingHarness h,
        CheckAvailabilityRequestDto quote, CreateReservationRequestDto create, string message, string? code = null)
    {
        var controller = Controller(h);
        var quoted = await controller.CheckAvailability(quote, null, CancellationToken.None);
        var created = await controller.Create(create, CancellationToken.None);
        foreach (var result in new[] { quoted.Result, created.Result })
        {
            var badRequest = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(message, JsonSerializer.SerializeToElement(badRequest.Value, Json)
                .GetProperty("error").GetString());
            if (code is not null)
                Assert.Equal(code, JsonSerializer.SerializeToElement(badRequest.Value, Json).GetProperty("code").GetString());
        }
        Assert.Empty(await h.CreateReservationService().ListMineAsync(h.CustomerId, CancellationToken.None));
    }

    private static CheckAvailabilityRequestDto Quote(LocationBookingHarness h, DateOnly? endDate) =>
        JsonSerializer.Deserialize<CheckAvailabilityRequestDto>(JsonSerializer.Serialize(new
        {
            assetId = h.AssetId, date = Date, endDate,
            startTime = new TimeOnly(8, 0), endTime = new TimeOnly(18, 0), quantity = 2,
        }, Json), Json)!;

    private static CreateReservationRequestDto Create(LocationBookingHarness h, DateOnly? endDate) =>
        JsonSerializer.Deserialize<CreateReservationRequestDto>(JsonSerializer.Serialize(new
        {
            unitId = h.UnitId, date = Date, endDate,
            startTime = new TimeOnly(8, 0), endTime = new TimeOnly(18, 0),
            items = new[] { new { assetId = h.AssetId, quantity = 2 } },
        }, Json), Json)!;

    private static async Task<LocationBookingHarness> CreateHarnessAsync()
    {
        var h = await LocationBookingHarness.CreateAsync();
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.Type = RentalAssetType.Good;
        rental.TotalQuantity = 2;
        rental.OpenTime = new(8, 0);
        rental.CloseTime = new(18, 0);
        rental.AllowedDurationMinutes = "600";
        foreach (var day in Enum.GetValues<DayOfWeek>())
            h.Db.RentalPricings.Add(new RentalPricing
            {
                TenantId = h.TenantId, RentalAssetId = h.RentalAssetId, DayOfWeek = day,
                StartTime = new(8, 0), EndTime = new(18, 0), PricingUnit = PricingUnit.Day,
                PricePerHour = 100m, RequiresDeposit = false, DepositPercentage = 0m,
            });
        await h.Db.SaveChangesAsync();
        return h;
    }
}
