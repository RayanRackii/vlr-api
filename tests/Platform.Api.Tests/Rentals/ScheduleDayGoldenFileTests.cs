using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

/// <summary>
/// Characterization snapshots of the agenda day read models over synthetic fixtures:
/// <c>GET /schedule/days/{date}</c> (admin and public, <see cref="ScheduleService.GetDayAsync"/>) and
/// <c>GET /public/tenants/{sub}/availability/days/{date}</c> (<see cref="DailyAvailabilityService"/>).
/// Golden files live in <c>GoldenFiles/ScheduleDay</c>. After an intended behavior change, rerun with
/// <c>UPDATE_GOLDEN_FILES=1</c> and review the JSON diff in the PR.
/// </summary>
public sealed class ScheduleDayGoldenFileTests
{
    private static readonly DateOnly Day = AvailabilityHarness.Day;

    [Fact]
    public async Task Civil_midnight_bounds_reservations_and_the_horizon_in_sao_paulo()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var names = Names(h);
        h.Primary.TotalQuantity = 2;
        h.Primary.OpenTime = new(0, 0);
        h.Primary.CloseTime = new(23, 59);
        h.Primary.AllowedDurationMinutes = "180";
        h.Primary.MaxAdvanceBookingDays = 0;
        Reserve(h, names, "touchesDayStart", 2, Local(Day.AddDays(-1), 22, 0), Local(Day, 0, 0));
        Reserve(h, names, "crossesIntoDay", 1, Local(Day.AddDays(-1), 23, 0), Local(Day, 1, 0));
        Reserve(h, names, "crossesOutOfDay", 2, Local(Day, 20, 30), Local(Day.AddDays(1), 0, 30));
        Reserve(h, names, "nextDay", 2, Local(Day.AddDays(1), 0, 0), Local(Day.AddDays(1), 2, 0));
        await h.Db.SaveChangesAsync();

        var snapshot = new Dictionary<string, object?>();
        // 23:30 in São Paulo is already Day in UTC; the civil "today" must still be the previous day.
        h.Clock.SetUtcNow(Local(Day.AddDays(-1), 23, 30));
        Assert.Equal(Day, DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime));
        snapshot["beforeCivilMidnight"] = await ReadAllAsync(h);
        h.Clock.SetUtcNow(Local(Day, 0, 30));
        snapshot["afterCivilMidnight"] = await ReadAllAsync(h);

        AssertGolden("timezone-midnight", snapshot, names);
    }

    [Fact]
    public async Task Overlapping_occupancy_precedence_tombstones_and_resource_order()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var names = Names(h);
        h.Primary.SchedulePolicy = SchedulePolicy.SlotGrid;
        // Inserted after Resource A but sorts first by name in both read models.
        var other = await h.AddResourceAsync(h.Tenant, h.Unit, "Court Z");
        names[other.Id] = "rental:Court Z";
        names[other.AssetId] = "asset:Court Z";
        var lesson = Kind(h, names, "lesson", "Aula", sortOrder: 1);
        var closed = Kind(h, names, "closed", "Fechado", sortOrder: 2);
        var maintenance = Kind(h, names, "maintenance", "Manutenção", sortOrder: 3);
        Template(h, names, "open 08-12", h.Open, new(8, 0), new(12, 0));
        Template(h, names, "lesson 09-10", lesson, new(9, 0), new(10, 0));
        Template(h, names, "maintenance 10:30-11", maintenance, new(10, 30), new(11, 0));
        Template(h, names, "closed 11-11:30", closed, new(11, 0), new(11, 30));
        // Persisted starts win over derived windows: a cancelled tombstone and a daily override.
        Slot(h, names, "tombstone 08-09", h.Open, new(8, 0), new(9, 0), SlotStatus.Cancelled, "private reason");
        Slot(h, names, "override 10-10:30", h.Open, new(10, 0), new(10, 30), SlotStatus.Available, "Promo");
        await h.Db.SaveChangesAsync();

        AssertGolden("occupancy-precedence", await ReadAllAsync(h), names);
    }

    [Fact]
    public async Task Complete_keeps_the_booked_slot_while_cancel_releases_it()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var names = Names(h);
        h.Primary.Type = RentalAssetType.Location;
        h.Primary.OpenTime = new(8, 0);
        h.Primary.CloseTime = new(12, 0);
        var slotCompleted = Reserve(h, names, "slotThenCompleted", 1, Local(Day, 8, 0), Local(Day, 9, 0));
        var slotCanceled = Reserve(h, names, "slotThenCanceled", 1, Local(Day, 9, 0), Local(Day, 10, 0));
        var derivedCompleted = Reserve(h, names, "derivedThenCompleted", 1, Local(Day, 10, 0), Local(Day, 11, 0));
        var derivedCanceled = Reserve(h, names, "derivedThenCanceled", 1, Local(Day, 11, 0), Local(Day, 12, 0));
        Slot(h, names, "booked 08-09", h.Open, new(8, 0), new(9, 0), SlotStatus.Booked).ReservationId = slotCompleted.Id;
        Slot(h, names, "booked 09-10", h.Open, new(9, 0), new(10, 0), SlotStatus.Booked).ReservationId = slotCanceled.Id;
        await h.Db.SaveChangesAsync();

        var snapshot = new Dictionary<string, object?> { ["beforeTransitions"] = await ReadAllAsync(h) };
        var reservations = Reservations(h);
        await reservations.CompleteAsync(slotCompleted.Id, default);
        await reservations.CancelAsync(slotCanceled.Id, default);
        await reservations.CompleteAsync(derivedCompleted.Id, default);
        await reservations.CancelAsync(derivedCanceled.Id, default);
        snapshot["transitionGuards"] = new
        {
            cancelCompleted = await CaptureAsync(() => reservations.CancelAsync(slotCompleted.Id, default)),
            completeCanceled = await CaptureAsync(() => reservations.CompleteAsync(slotCanceled.Id, default)),
        };
        snapshot["afterTransitions"] = await ReadAllAsync(h);
        snapshot["reservationStatuses"] = await h.Db.Reservations.AsNoTracking()
            .OrderBy(r => r.StartDateTime).Select(r => new { r.Id, r.Status }).ToListAsync();

        AssertGolden("complete-vs-cancel", snapshot, names);
    }

    [Fact]
    public async Task Waiting_queue_turns_do_not_hold_capacity_or_change_on_read()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var names = Names(h);
        h.Primary.Type = RentalAssetType.Location;
        h.Primary.QueueEnabled = true;
        h.Primary.QueueOpeningTime = new(7, 30);
        var first = Customer(h, names, "customer:first");
        var second = Customer(h, names, "customer:second");
        await h.Db.SaveChangesAsync();
        var queue = TestReservationQueue.Create(h.Db, h.TenantProvider, h.Clock);
        await queue.JoinAsync(h.Primary.Id, first.Id, default);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await queue.JoinAsync(h.Primary.Id, second.Id, default);

        var ticketsBefore = await TicketsAsync(h);
        var reads = await ReadAllAsync(h);
        var ticketsAfter = await TicketsAsync(h);
        Assert.Equal(JsonSerializer.Serialize(ticketsBefore, Json), JsonSerializer.Serialize(ticketsAfter, Json));

        AssertGolden("queue", new { tickets = ticketsBefore, reads }, names);
    }

    private static async Task<object> ReadAllAsync(AvailabilityHarness h) => new
    {
        now = h.Clock.GetUtcNow(),
        availability = await CaptureAsync(() => h.ReadAsync()),
        publicDay = await CaptureAsync(() => Schedule(h).GetDayAsync(Day, null, customerFacing: true, default)),
        adminDay = await CaptureAsync(() => Schedule(h).GetDayAsync(Day, null, customerFacing: false, default)),
    };

    private static async Task<object?> CaptureAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception ex)
        {
            return new { error = ex.GetType().Name, ex.Message };
        }
    }

    private static Task<List<object>> TicketsAsync(AvailabilityHarness h) =>
        h.Db.ReservationQueueTickets.AsNoTracking().OrderBy(t => t.Sequence)
            .Select(t => (object)new { t.Sequence, t.CustomerId, t.Status, t.TurnStartedAt, t.TurnExpiresAt })
            .ToListAsync();

    private static ScheduleService Schedule(AvailabilityHarness h) =>
        new(h.Db, h.TenantProvider, new OccupancyKindService(h.Db, h.TenantProvider), new FakeTrialGuard(),
            TestReservationQueue.Create(h.Db, h.TenantProvider, h.Clock),
            SilentRentalsNotifications.Publisher.AsDomainEvents(), SilentRentalsNotifications.Scheduler, h.Clock);

    private static ReservationService Reservations(AvailabilityHarness h) =>
        new(h.Db, h.TenantProvider, new FakeTrialGuard(), TestReservationQueue.Create(h.Db, h.TenantProvider, h.Clock),
            SilentRentalsNotifications.Publisher.AsDomainEvents(), SilentRentalsNotifications.Scheduler, h.Clock);

    private static DateTimeOffset Local(DateOnly date, int hour, int minute) =>
        BrazilTimeZone.AtLocal(date, new TimeOnly(hour, minute));

    private static Dictionary<Guid, string> Names(AvailabilityHarness h) => new()
    {
        [h.Tenant.Id] = "tenant",
        [h.Unit.Id] = "unit",
        [h.Open.Id] = "kind:open",
        [h.Primary.Id] = "rental:Resource A",
        [h.Primary.AssetId] = "asset:Resource A",
    };

    private static Customer Customer(AvailabilityHarness h, Dictionary<Guid, string> names, string name)
    {
        var customer = new Customer { TenantId = h.Tenant.Id, Name = name, Phone = "5511000000000" };
        h.Db.Customers.Add(customer);
        names[customer.Id] = name;
        return customer;
    }

    private static Reservation Reserve(AvailabilityHarness h, Dictionary<Guid, string> names, string name,
        int quantity, DateTimeOffset start, DateTimeOffset end)
    {
        var customer = Customer(h, names, $"customer:{name}");
        var reservation = new Reservation { TenantId = h.Tenant.Id, UnitId = h.Unit.Id, CustomerId = customer.Id,
            CustomerName = name, CustomerWhatsApp = "5511000000000", Status = ReservationStatus.Confirmed,
            StartDateTime = start, EndDateTime = end, TotalAmount = 20, DepositPaid = 0 };
        reservation.AddItem(new ReservationItem { TenantId = h.Tenant.Id, ReservationId = reservation.Id,
            RentalAssetId = h.Primary.Id, Quantity = quantity, UnitPrice = 20, SubTotal = 20 });
        h.Db.Reservations.Add(reservation);
        names[reservation.Id] = $"reservation:{name}";
        return reservation;
    }

    private static OccupancyKind Kind(AvailabilityHarness h, Dictionary<Guid, string> names,
        string key, string label, int sortOrder)
    {
        var kind = new OccupancyKind { TenantId = h.Tenant.Id, Key = key, Label = label, IsActive = true,
            IsBookableByCustomer = false, BlocksCapacity = true, SortOrder = sortOrder };
        h.Db.OccupancyKinds.Add(kind);
        names[kind.Id] = $"kind:{key}";
        return kind;
    }

    private static void Template(AvailabilityHarness h, Dictionary<Guid, string> names, string name,
        OccupancyKind kind, TimeOnly start, TimeOnly end)
    {
        var template = new ScheduleTemplate { TenantId = h.Tenant.Id, RentalAssetId = h.Primary.Id,
            DayOfWeek = Day.DayOfWeek, StartTime = start, EndTime = end, OccupancyKindId = kind.Id, IsActive = true };
        h.Db.ScheduleTemplates.Add(template);
        names[template.Id] = $"template:{name}";
    }

    private static Slot Slot(AvailabilityHarness h, Dictionary<Guid, string> names, string name,
        OccupancyKind kind, TimeOnly start, TimeOnly end, SlotStatus status, string? label = null)
    {
        var slot = new Slot { TenantId = h.Tenant.Id, RentalAssetId = h.Primary.Id, Date = Day, StartTime = start,
            EndTime = end, OccupancyKindId = kind.Id, Status = status, Label = label };
        h.Db.Slots.Add(slot);
        names[slot.Id] = $"slot:{name}";
        return slot;
    }

    // Mirrors the API wire format (Program.cs: web defaults + string enums).
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Regex GuidPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    private static void AssertGolden(string name, object snapshot, IReadOnlyDictionary<Guid, string> names,
        [CallerFilePath] string sourceFile = "")
    {
        // Fixture ids are random per run; named ids become stable tokens, unknown ones are numbered by first use.
        var unknown = new Dictionary<Guid, string>();
        var actual = GuidPattern.Replace(JsonSerializer.Serialize(snapshot, Json), match =>
        {
            var id = Guid.Parse(match.Value);
            if (id == Guid.Empty) return match.Value;
            if (names.TryGetValue(id, out var known)) return $"<{known}>";
            if (!unknown.TryGetValue(id, out var token)) unknown[id] = token = $"<guid#{unknown.Count + 1}>";
            return token;
        }) + "\n";
        var path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "GoldenFiles", "ScheduleDay", $"{name}.json");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN_FILES") == "1" || !File.Exists(path))
        {
            var created = !File.Exists(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            Assert.False(created, $"Golden file created at {path}; review it and rerun.");
            return;
        }
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
