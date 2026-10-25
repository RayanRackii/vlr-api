using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.CustomerAuth;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class DailyAvailabilityTests
{
    [Theory]
    [InlineData(9, 0, 10, 0)]
    [InlineData(12, 0, 13, 0)]
    [InlineData(10, 0, 10, 30)]
    public async Task Invalid_persisted_OpenHours_slots_are_blocked(
        int startHour, int startMinute, int endHour, int endMinute)
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var slot = h.Slot(startHour, endHour, SlotStatus.Available);
        slot.StartTime = new(startHour, startMinute);
        slot.EndTime = new(endHour, endMinute);
        h.Db.Slots.Add(slot);
        await h.Db.SaveChangesAsync();

        var windows = Assert.Single((await h.ReadAsync()).Resources).Windows;
        var window = Assert.Single(windows, w => w.SlotId == slot.Id);
        Assert.Equal(AvailabilityWindowState.Blocked, window.State);
        Assert.Equal(0, window.AvailableQuantity);
        Assert.Contains(windows, w => w.State == AvailabilityWindowState.Available);
    }

    [Theory]
    [InlineData(SlotStatus.Booked, AvailabilityWindowState.Taken)]
    [InlineData(SlotStatus.Cancelled, AvailabilityWindowState.Blocked)]
    public async Task Invalid_persisted_OpenHours_slots_keep_occupied_and_blocked_windows_visible(
        SlotStatus status, AvailabilityWindowState expectedState)
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var slot = h.Slot(9, 10, status);
        h.Db.Slots.Add(slot);
        await h.Db.SaveChangesAsync();
        if (status == SlotStatus.Booked)
            await h.ReserveAsync(h.Primary, 1, new(9, 0), new(10, 0));

        var window = Assert.Single((await h.ReadAsync()).Resources[0].Windows, w => w.SlotId == slot.Id);
        Assert.Equal(expectedState, window.State);
    }

    [Fact]
    public async Task DST_gap_interval_that_inverts_in_UTC_is_omitted()
    {
        var date = new DateOnly(2026, 3, 8);
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Db.TenantSettings.Add(new TenantSetting
        {
            TenantId = h.Tenant.Id,
            Key = Platform.Core.Domain.Constants.TenantSettingKeys.BookingTimeZoneId,
            Value = "America/New_York",
        });
        h.Primary.OpenTime = new TimeOnly(2, 30);
        h.Primary.CloseTime = new TimeOnly(3, 0);
        h.Primary.AllowedDurationMinutes = "30";
        h.Clock.SetUtcNow(new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero));
        await h.Db.SaveChangesAsync();

        var response = await h.ReadAsync(date: date);

        Assert.Empty(Assert.Single(response.Resources).Windows);
    }

    [Fact]
    public async Task Derived_occupied_windows_and_requested_stock_quantity_remain_visible_with_prices()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        await h.ReserveAsync(h.Primary, 2, new(10, 0), new(11, 0));
        var day = await h.ReadAsync(quantity: 4);
        var resource = Assert.Single(day.Resources);
        var occupied = Assert.Single(resource.Windows, w => w.StartTime == new TimeOnly(10, 0));
        Assert.Equal(AvailabilityWindowState.Taken, occupied.State);
        Assert.Equal(3, occupied.AvailableQuantity);
        Assert.Equal(80m, occupied.TotalAmount);
        Assert.Null(occupied.SlotId);
        Assert.True(occupied.IsDerived);
        Assert.Equal(AvailabilityWindowState.Available, resource.Windows[1].State);
        Assert.Equal("BRL", day.Currency);
        Assert.Equal("America/Sao_Paulo", day.TimeZone);
        Assert.Equal(TimeSpan.FromSeconds(60), day.QuoteExpiresAt - day.ServerNow);
        Assert.DoesNotContain(h.Db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    [Fact]
    public async Task Persisted_and_derived_prices_match_their_respective_booking_precedence()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Db.RentalPricings.Add(AvailabilityHarness.Price(h.Primary, 10, 12, 70));
        h.Db.Slots.Add(h.Slot(10, 11, SlotStatus.Available));
        await h.Db.SaveChangesAsync();
        var windows = Assert.Single((await h.ReadAsync()).Resources).Windows;
        Assert.Equal(70m, windows[0].TotalAmount);
        Assert.Equal(20m, windows[1].TotalAmount);
        Assert.False(windows[0].IsDerived);
        Assert.True(windows[1].IsDerived);
    }

    [Fact]
    public async Task Different_durations_keep_full_interval_identity_and_round_quantity_totals()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.AllowedDurationMinutes = "30,60";
        foreach (var price in await h.Db.RentalPricings.ToListAsync()) price.PricePerHour = 20.01m;
        await h.Db.SaveChangesAsync();
        var windows = (await h.ReadAsync(quantity: 3)).Resources[0].Windows;
        var sameStart = windows.Where(w => w.StartTime == new TimeOnly(10, 0)).ToArray();
        Assert.Equal(2, sameStart.Length);
        Assert.Equal(new TimeOnly(10, 30), sameStart[0].EndTime);
        Assert.Equal(30.02m, sameStart[0].TotalAmount);
        Assert.Equal(new TimeOnly(11, 0), sameStart[1].EndTime);
        Assert.Equal(60.03m, sameStart[1].TotalAmount);
        var filtered = (await h.ReadAsync(quantity: 3, duration: 30)).Resources[0].Windows;
        Assert.Equal(4, filtered.Count);
        Assert.All(filtered, w => Assert.Equal(TimeSpan.FromMinutes(30), w.EndTime - w.StartTime));
    }

    [Fact]
    public async Task Zero_is_priced_and_null_is_unpriced_even_when_taken()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Db.RentalPricings.RemoveRange(await h.Db.RentalPricings.ToListAsync());
        h.Db.RentalPricings.Add(AvailabilityHarness.Price(h.Primary, 10, 11, 0));
        await h.ReserveAsync(h.Primary, 5, new(11, 0), new(12, 0));
        var windows = Assert.Single((await h.ReadAsync()).Resources).Windows;
        Assert.Equal(AvailabilityWindowState.Available, windows[0].State);
        Assert.Equal(0, windows[0].TotalAmount);
        Assert.Equal(AvailabilityWindowState.Taken, windows[1].State);
        Assert.Null(windows[1].TotalAmount);
        h.Db.Reservations.RemoveRange(await h.Db.Reservations.ToListAsync());
        h.Db.ReservationItems.RemoveRange(await h.Db.ReservationItems.ToListAsync());
        await h.Db.SaveChangesAsync();
        Assert.Equal(AvailabilityWindowState.Unpriced, Assert.Single((await h.ReadAsync()).Resources).Windows[1].State);
    }

    [Fact]
    public async Task Blocked_occurrences_override_open_hours_without_leaking_private_labels()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var blocked = h.Slot(10, 11, SlotStatus.Cancelled);
        blocked.Label = "private reason 123";
        h.Db.Slots.Add(blocked);
        await h.Db.SaveChangesAsync();
        var day = await h.ReadAsync();
        Assert.Equal(AvailabilityWindowState.Blocked, day.Resources[0].Windows[0].State);
        Assert.Equal(0, day.Resources[0].Windows[0].AvailableQuantity);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(day), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_or_inactive_kind_blocks_overlapping_derived_windows()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var invalid = h.Slot(10, 11, SlotStatus.Available);
        invalid.StartTime = new(10, 30);
        invalid.OccupancyKindId = Guid.NewGuid();
        h.Db.Slots.Add(invalid);
        await h.Db.SaveChangesAsync();
        Assert.All((await h.ReadAsync()).Resources[0].Windows.Where(w => w.StartTime < new TimeOnly(11, 0)),
            w => Assert.Equal(AvailabilityWindowState.Blocked, w.State));
    }

    [Fact]
    public async Task Persisted_booked_stock_stays_taken_with_remaining_physical_capacity()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Db.Slots.Add(h.Slot(10, 11, SlotStatus.Booked));
        await h.ReserveAsync(h.Primary, 1, new(10, 0), new(11, 0));
        var window = (await h.ReadAsync()).Resources[0].Windows[0];
        Assert.Equal(AvailabilityWindowState.Taken, window.State);
        Assert.Equal(4, window.AvailableQuantity);
    }

    [Fact]
    public async Task Mixed_horizons_keep_resources_and_all_outside_preserves_legacy_error()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.MaxAdvanceBookingDays = 0;
        var other = await h.AddResourceAsync(h.Tenant, h.Unit, "Resource B");
        other.MaxAdvanceBookingDays = 2;
        await h.Db.SaveChangesAsync();
        var day = await h.ReadAsync(date: AvailabilityHarness.Day.AddDays(1));
        Assert.Equal(2, day.Resources.Count);
        Assert.All(day.Resources.Single(r => r.RentalAssetId == h.Primary.Id).Windows,
            w => Assert.Equal(AvailabilityWindowState.OutsideHorizon, w.State));
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() => h.ReadAsync(date: AvailabilityHarness.Day.AddDays(3)));
    }

    [Fact]
    public async Task Past_precedes_blocking_and_exact_now_is_available()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Clock.SetUtcNow(BrazilTimeZone.AtLocal(AvailabilityHarness.Day, new(10, 0)));
        Assert.Equal(AvailabilityWindowState.Available, (await h.ReadAsync()).Resources[0].Windows[0].State);
        h.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(AvailabilityWindowState.Past, (await h.ReadAsync()).Resources[0].Windows[0].State);
    }

    [Fact]
    public async Task Slot_grid_reuses_precedence_and_keeps_taken_derived_intervals()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.SchedulePolicy = SchedulePolicy.SlotGrid;
        var closed = new OccupancyKind { TenantId = h.Tenant.Id, Key = "closed", Label = "Private",
            IsActive = true, IsBookableByCustomer = false, BlocksCapacity = true, SortOrder = 2 };
        h.Db.OccupancyKinds.Add(closed);
        h.Db.ScheduleTemplates.AddRange(h.Template(10, 12, h.Open), h.Template(11, 12, closed));
        await h.ReserveAsync(h.Primary, 5, new(10, 0), new(11, 0));
        var windows = (await h.ReadAsync()).Resources[0].Windows;
        Assert.Equal(2, windows.Count);
        Assert.Equal(AvailabilityWindowState.Taken, windows[0].State);
        Assert.Equal(AvailabilityWindowState.Blocked, windows[1].State);
    }

    [Fact]
    public async Task Foreign_ids_and_unit_do_not_broaden_scope_and_foreign_reservations_do_not_reduce_capacity()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var foreign = new Tenant("Other", "22222222000191", subdomain: "other");
        var unit = new Unit(foreign.Id, "Other");
        h.Db.AddRange(foreign, unit);
        var resource = await h.AddResourceAsync(foreign, unit, "Hidden");
        await h.ReserveAsync(resource, 5, new(10, 0), new(12, 0));
        Assert.Empty((await h.ReadAsync(ids: [resource.Id])).Resources);
        Assert.Empty((await h.ReadAsync(unitId: unit.Id)).Resources);
        Assert.Single((await h.ReadAsync(ids: [h.Primary.Id, resource.Id])).Resources);
        Assert.All((await h.ReadAsync()).Resources[0].Windows, w => Assert.Equal(5, w.AvailableQuantity));
    }

    [Theory]
    [InlineData(0, null)] [InlineData(-1, 60)] [InlineData(1, 0)] [InlineData(1, 1441)]
    public async Task Invalid_quantity_and_duration_fail_with_stable_code(int quantity, int? duration)
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var ex = await Assert.ThrowsAsync<AvailabilityRequestException>(() => h.ReadAsync(quantity: quantity, duration: duration));
        Assert.Equal("AVAILABILITY_INVALID_QUERY", ex.Code);
    }

    [Fact]
    public async Task Invalid_date_ids_limits_and_missing_tenant_are_rejected()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        await Assert.ThrowsAsync<AvailabilityRequestException>(() => h.ReadAsync(date: DateOnly.MaxValue));
        await Assert.ThrowsAsync<AvailabilityRequestException>(() => h.ReadAsync(ids: [Guid.Empty]));
        var limit = await Assert.ThrowsAsync<AvailabilityRequestException>(() =>
            h.ReadAsync(ids: Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray()));
        Assert.Equal("AVAILABILITY_LIMIT_EXCEEDED", limit.Code);
        h.TenantProvider.TenantId = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.ReadAsync());
        h.TenantProvider.TenantId = Guid.Empty;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.ReadAsync());
    }

    [Fact]
    public async Task Missing_kind_catalog_is_not_seeded_by_read()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Db.OccupancyKinds.Remove(h.Open);
        await h.Db.SaveChangesAsync();
        Assert.All((await h.ReadAsync()).Resources[0].Windows, w => Assert.Equal(AvailabilityWindowState.Blocked, w.State));
        Assert.Empty(await h.Db.OccupancyKinds.ToListAsync());
    }

    [Fact]
    public async Task Physical_capacity_ignores_queue_and_inactive_reservations_and_honors_half_open_overlaps()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        await h.ReserveAsync(h.Primary, 2, new(10, 0), new(11, 0));
        var reservation = await h.Db.Reservations.SingleAsync();
        reservation.StartDateTime = BrazilTimeZone.AtLocal(AvailabilityHarness.Day.AddDays(-1), new(23, 0));
        await h.Db.SaveChangesAsync();
        var windows = (await h.ReadAsync()).Resources[0].Windows;
        Assert.Equal(3, windows[0].AvailableQuantity);
        Assert.Equal(5, windows[1].AvailableQuantity); // touching end/start does not overlap
        // Completing never releases capacity; only cancelling does.
        reservation.Status = ReservationStatus.Completed;
        await h.Db.SaveChangesAsync();
        Assert.Equal([3, 5], (await h.ReadAsync()).Resources[0].Windows.Select(w => w.AvailableQuantity));
        reservation.Status = ReservationStatus.Canceled;
        await h.Db.SaveChangesAsync();
        Assert.All((await h.ReadAsync()).Resources[0].Windows, w => Assert.Equal(5, w.AvailableQuantity));
        h.Primary.Type = RentalAssetType.Location;
        h.Primary.QueueEnabled = true;
        h.Primary.RequiresDeposit = true;
        await h.Db.SaveChangesAsync();
        var resource = (await h.ReadAsync()).Resources[0];
        Assert.True(resource.QueueEnabled);
        Assert.True(resource.RequiresDeposit);
        Assert.Equal(1, resource.TotalQuantity);
        Assert.All(resource.Windows, w => { Assert.Equal(1, w.AvailableQuantity); Assert.Equal(AvailabilityWindowState.Available, w.State); });
    }

    [Fact]
    public async Task Horizon_is_inclusive_and_null_is_unlimited_with_empty_duration_results_preserved()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        h.Primary.MaxAdvanceBookingDays = 2;
        await h.Db.SaveChangesAsync();
        Assert.All((await h.ReadAsync(date: AvailabilityHarness.Day.AddDays(2))).Resources[0].Windows,
            w => Assert.Equal(AvailabilityWindowState.Available, w.State));
        h.Primary.MaxAdvanceBookingDays = null;
        await h.Db.SaveChangesAsync();
        Assert.All((await h.ReadAsync(date: AvailabilityHarness.Day.AddYears(2))).Resources[0].Windows,
            w => Assert.Equal(AvailabilityWindowState.Available, w.State));
        Assert.Empty((await h.ReadAsync(duration: 90)).Resources[0].Windows);
    }

    [Fact]
    public async Task Resource_and_window_limits_reject_instead_of_truncating()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        for (var i = 0; i < 8; i++) await h.AddResourceAsync(h.Tenant, h.Unit, $"Resource {i}");
        foreach (var resource in await h.Db.RentalAssets.ToListAsync())
        { resource.OpenTime = new(0, 0); resource.CloseTime = new(23, 0); resource.AllowedDurationMinutes = "1"; }
        await h.Db.SaveChangesAsync();
        Assert.Equal("AVAILABILITY_LIMIT_EXCEEDED", (await Assert.ThrowsAsync<AvailabilityRequestException>(() => h.ReadAsync())).Code);
        for (var i = 9; i < 101; i++) await h.AddResourceAsync(h.Tenant, h.Unit, $"Resource {i}");
        Assert.Equal("AVAILABILITY_LIMIT_EXCEEDED", (await Assert.ThrowsAsync<AvailabilityRequestException>(() => h.ReadAsync())).Code);
    }

    [Theory]
    [InlineData(null, "America/Sao_Paulo")]
    [InlineData("Not/AZone", "America/Sao_Paulo")]
    [InlineData("Asia/Tokyo", "Asia/Tokyo")]
    [InlineData("E. South America Standard Time", "America/Sao_Paulo")]
    public async Task Branding_returns_effective_iana_zone_for_the_requested_tenant(string? configured, string expected)
    {
        await using var h = await CustomerAuthHarness.CreateAsync();
        if (configured is not null)
        {
            h.Db.TenantSettings.Add(new TenantSetting { TenantId = h.Tenant.Id,
                Key = Platform.Core.Domain.Constants.TenantSettingKeys.BookingTimeZoneId, Value = configured });
        }
        var other = new Tenant("Other", "22222222000191", subdomain: "other-zone");
        h.Db.Tenants.Add(other);
        h.Db.TenantSettings.Add(new TenantSetting { TenantId = other.Id,
            Key = Platform.Core.Domain.Constants.TenantSettingKeys.BookingTimeZoneId, Value = "America/Manaus" });
        await h.Db.SaveChangesAsync();
        Assert.Equal(expected, (await h.Auth.GetBrandingAsync(h.Tenant.Subdomain!, default)).TimeZoneId);
        Assert.Equal("America/Manaus", (await h.Auth.GetBrandingAsync("other-zone", default)).TimeZoneId);
    }

    [Fact]
    public async Task Branding_flag_defaults_off_and_configuration_selects_only_the_exact_tenant()
    {
        await using var h = await CustomerAuthHarness.CreateAsync();
        Assert.False((await h.Auth.GetBrandingAsync(h.Tenant.Subdomain!, default)).NewBookingUiEnabled);
        var options = Options.Create(new BookingUiOptions { EnabledTenantIds = [h.Tenant.Id] });
        var auth = CustomerAuthHarness.CreateService(h.Db, h.TenantProvider, h.Phone, h.Email,
            new AllowAllPhoneVerificationSendGate(), h.Time, bookingUiOptions: options);
        Assert.True((await auth.GetBrandingAsync(h.Tenant.Subdomain!, default)).NewBookingUiEnabled);
        var other = new Tenant("Other", "22222222000191", subdomain: "other");
        h.Db.Tenants.Add(other);
        await h.Db.SaveChangesAsync();
        Assert.False((await auth.GetBrandingAsync("other", default)).NewBookingUiEnabled);
        Assert.False(options.Value.IsEnabled(Guid.Empty));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Rentals:BookingUi:EnabledTenantIds:0"] = h.Tenant.Id.ToString() }).Build());
        services.AddRentalsModule();
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<IOptions<BookingUiOptions>>().Value.IsEnabled(h.Tenant.Id));
    }

    [Fact]
    public void Window_states_preserve_contract_case_with_the_global_camel_case_converter()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var dto = new AvailabilityWindowDto(null, new(10, 0), new(11, 0), true,
            AvailabilityWindowState.OutsideHorizon, "booking_horizon_exceeded", 1, 20);
        Assert.Equal("OutsideHorizon", JsonSerializer.SerializeToElement(dto, options).GetProperty("state").GetString());
    }
}

internal sealed class AvailabilityHarness : IAsyncDisposable
{
    public static readonly DateOnly Day = new(2026, 10, 21);
    public FakeTenantProvider TenantProvider { get; } = new();
    public AppDbContext Db { get; }
    public Tenant Tenant { get; } = new("Example", "11111111000191", subdomain: "example");
    public Unit Unit { get; }
    public RentalAsset Primary { get; private set; } = null!;
    public OccupancyKind Open { get; }
    public TestTimeProvider Clock { get; } = new(BrazilTimeZone.AtLocal(Day, new(8, 0)));
    public string DatabaseName { get; } = Guid.NewGuid().ToString("N");
    private AvailabilityHarness()
    {
        Db = InMemoryAppDb.Create(TenantProvider, DatabaseName);
        TenantProvider.TenantId = Tenant.Id;
        Unit = new(Tenant.Id, "Main");
        Open = new() { TenantId = Tenant.Id, Key = "open", Label = "Open", IsActive = true,
            IsBookableByCustomer = true, BlocksCapacity = false, SortOrder = 0 };
    }
    public static async Task<AvailabilityHarness> CreateAsync()
    {
        var h = new AvailabilityHarness();
        h.Db.AddRange(h.Tenant, h.Unit, h.Open, new TenantModule(h.Tenant.Id, "rentals"));
        h.Primary = await h.AddResourceAsync(h.Tenant, h.Unit, "Resource A");
        return h;
    }
    public async Task<RentalAsset> AddResourceAsync(Tenant tenant, Unit unit, string name)
    {
        var category = new AssetCategory { TenantId = tenant.Id, Name = $"Resources {name}" };
        var family = new AssetFamily { Key = Guid.NewGuid().ToString("N"), Label = "Resources", FieldSchemaJson = "{}" };
        var asset = new Asset { TenantId = tenant.Id, UnitId = unit.Id, CategoryId = category.Id,
            FamilyId = family.Id, Name = name, Tag = name, Status = AssetStatus.Active, IsRentable = true };
        var rental = new RentalAsset { TenantId = tenant.Id, AssetId = asset.Id, Type = RentalAssetType.Good,
            TotalQuantity = 5, IsActive = true, RequiresDeposit = false, SchedulePolicy = SchedulePolicy.OpenHours,
            OpenTime = new(10, 0), CloseTime = new(12, 0), AllowedDurationMinutes = "60" };
        Db.AddRange(category, family, asset, rental);
        // OpenHours and pricing are civil; every weekday allows tests of mixed horizons.
        foreach (var weekday in Enum.GetValues<DayOfWeek>()) Db.Add(Price(rental, 0, 23, 20, weekday));
        await Db.SaveChangesAsync();
        return rental;
    }
    public static RentalPricing Price(RentalAsset resource, int start, int end, decimal amount, DayOfWeek? weekday = null) =>
        new() { TenantId = resource.TenantId, RentalAssetId = resource.Id, DayOfWeek = weekday ?? Day.DayOfWeek,
            StartTime = new(start, 0), EndTime = new(end, 0), PricePerHour = amount, RequiresDeposit = false, DepositPercentage = 0 };
    public Slot Slot(int start, int end, SlotStatus status) => new()
    { TenantId = Tenant.Id, RentalAssetId = Primary.Id, Date = Day, StartTime = new(start, 0), EndTime = new(end, 0),
        OccupancyKindId = Open.Id, Status = status };
    public ScheduleTemplate Template(int start, int end, OccupancyKind kind) => new()
    { TenantId = Tenant.Id, RentalAssetId = Primary.Id, DayOfWeek = Day.DayOfWeek, StartTime = new(start, 0),
        EndTime = new(end, 0), OccupancyKindId = kind.Id, IsActive = true };
    public async Task ReserveAsync(RentalAsset resource, int quantity, TimeOnly start, TimeOnly end)
    {
        var customer = new Customer { TenantId = resource.TenantId, Name = "Private customer", Phone = "5511000000000" };
        var reservation = new Reservation { TenantId = resource.TenantId, UnitId = Unit.Id, CustomerId = customer.Id,
            CustomerName = "Private customer", CustomerWhatsApp = "Private phone", Status = ReservationStatus.Confirmed,
            StartDateTime = BrazilTimeZone.AtLocal(Day, start), EndDateTime = BrazilTimeZone.AtLocal(Day, end),
            TotalAmount = 20, DepositPaid = 0 };
        var item = new ReservationItem { TenantId = resource.TenantId, ReservationId = reservation.Id,
            RentalAssetId = resource.Id, Quantity = quantity, UnitPrice = 20, SubTotal = 20 };
        reservation.AddItem(item);
        Db.AddRange(customer, reservation);
        await Db.SaveChangesAsync();
    }
    public Task<DailyAvailabilityResponseDto> ReadAsync(DateOnly? date = null, Guid[]? ids = null,
        Guid? unitId = null, int quantity = 1, int? duration = null) =>
        new DailyAvailabilityService(Db, TenantProvider, Clock).GetAsync(date ?? Day, ids, unitId, quantity, duration, default);
    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
