using Microsoft.EntityFrameworkCore;
using Platform.Api.DomainEvents;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Events;

namespace Platform.Api.Tests.Rentals;

public sealed class StaffReservationTests
{
    [Fact]
    public async Task Existing_customer_is_confirmed_by_default_and_emits_the_confirmed_event()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);
        var events = new RecordingEvents();

        var created = await Service(h, events).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA }, CancellationToken.None);

        Assert.Equal(ReservationStatus.Confirmed, created.Status);
        Assert.Equal(0m, created.DepositPaid);
        Assert.Equal(h.CustomerA, created.CustomerId);
        Assert.Equal(100m, created.TotalAmount);
        var stored = await h.Db.Reservations.SingleAsync();
        Assert.Equal(ReservationPurpose.CustomerBooking, stored.Purpose);
        Assert.Equal(ReservationStatus.Confirmed, stored.Status);
        Assert.Equal(RentalEventTypes.ReservationConfirmed, Assert.Single(events.Types));
    }

    [Fact]
    public async Task Awaiting_deposit_on_a_deposit_resource_opens_pending_deposit_with_the_pending_event()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);
        var events = new RecordingEvents();

        var created = await Service(h, events).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA, AwaitingDeposit = true }, CancellationToken.None);

        Assert.Equal(ReservationStatus.PendingDeposit, created.Status);
        Assert.Equal(0m, created.DepositPaid);
        Assert.Equal(RentalEventTypes.ReservationPendingDeposit, Assert.Single(events.Types));
    }

    [Fact]
    public async Task Awaiting_deposit_on_a_resource_without_deposit_stays_confirmed()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);
        (await h.Db.RentalAssets.SingleAsync()).RequiresDeposit = false;
        await h.Db.SaveChangesAsync();
        var events = new RecordingEvents();

        var created = await Service(h, events).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA, AwaitingDeposit = true }, CancellationToken.None);

        Assert.Equal(ReservationStatus.Confirmed, created.Status);
        Assert.Equal(RentalEventTypes.ReservationConfirmed, Assert.Single(events.Types));
    }

    [Fact]
    public async Task New_customer_is_created_with_a_normalised_phone_and_no_password()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);

        var created = await Service(h).CreateStaffReservationAsync(
            StaffRequest(h) with
            {
                NewCustomer = new StaffNewCustomerDto
                {
                    Name = "  Maria Souza ",
                    WhatsApp = "(11) 98888-7777",
                    Email = " Maria@Mail.Test ",
                },
            },
            CancellationToken.None);

        var customer = await h.Db.Customers.SingleAsync(c => c.Id == created.CustomerId);
        Assert.Equal(h.TenantId, customer.TenantId);
        Assert.Equal("Maria Souza", customer.Name);
        Assert.Equal("+5511988887777", customer.Phone);
        Assert.Equal("maria@mail.test", customer.Email);
        Assert.Null(customer.PasswordHash);
        Assert.Null(customer.EmailVerifiedAt);
        Assert.Equal("Maria Souza", created.CustomerName);
        Assert.Equal("+5511988887777", created.CustomerWhatsApp);
        Assert.Equal(ReservationStatus.Confirmed, created.Status);
    }

    [Theory]
    [InlineData("11 97777-6666", null)]
    [InlineData("11 96666-5555", "taken@mail.test")]
    public async Task Duplicate_phone_or_email_is_refused_with_the_existing_customer_id(string whatsApp, string? email)
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);
        var existing = new Customer
        {
            TenantId = h.TenantId,
            Name = "Ja Cadastrado",
            Phone = "+5511977776666",
            Email = "taken@mail.test",
        };
        h.Db.Customers.Add(existing);
        await h.Db.SaveChangesAsync();
        var customersBefore = await h.Db.Customers.CountAsync();

        // The phone matches in the first case; the e-mail matches in the second.
        var error = await Assert.ThrowsAsync<CustomerAlreadyExistsException>(() =>
            Service(h).CreateStaffReservationAsync(
                StaffRequest(h) with
                {
                    NewCustomer = new StaffNewCustomerDto { Name = "Outra", WhatsApp = whatsApp, Email = email },
                },
                CancellationToken.None));

        Assert.Equal(existing.Id, error.CustomerId);
        Assert.Equal("CUSTOMER_EXISTS", CustomerAlreadyExistsException.Code);
        Assert.Equal(customersBefore, await h.Db.Customers.CountAsync());
        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Fact]
    public async Task Customer_of_another_tenant_is_not_found()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);
        var foreign = new Customer { TenantId = Guid.NewGuid(), Name = "Outro Clube", Phone = "+5511955554444" };
        h.Db.Customers.Add(foreign);
        await h.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Service(h).CreateStaffReservationAsync(
                StaffRequest(h) with { CustomerId = foreign.Id }, CancellationToken.None));

        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Fact]
    public async Task Exactly_one_of_customer_id_or_new_customer_is_required()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(h).CreateStaffReservationAsync(StaffRequest(h), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service(h).CreateStaffReservationAsync(
                StaffRequest(h) with
                {
                    CustomerId = h.CustomerA,
                    NewCustomer = new StaffNewCustomerDto { Name = "Maria", WhatsApp = "11988887777" },
                },
                CancellationToken.None));
    }

    [Fact]
    public async Task Overlapping_booking_is_refused_like_the_portal()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 2);
        await Service(h).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA }, CancellationToken.None);

        var staffError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(h).CreateStaffReservationAsync(
                StaffRequest(h) with { CustomerId = h.CustomerB }, CancellationToken.None));
        var portalError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(h).CreateReservationAsync(h.CustomerB, h.BookRequest(), CancellationToken.None));

        Assert.Equal(portalError.Message, staffError.Message);
        Assert.Single(await h.Db.Reservations.ToListAsync());
    }

    [Fact]
    public async Task Start_in_the_past_is_refused()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);

        await Assert.ThrowsAsync<StartInThePastException>(() =>
            Service(h).CreateStaffReservationAsync(
                StaffRequest(h) with
                {
                    CustomerId = h.CustomerA,
                    Date = ReservationQueueHarness.BookDate.AddDays(-1),
                },
                CancellationToken.None));

        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Fact]
    public async Task Horizon_lead_time_and_daily_limit_bind_the_portal_but_not_staff()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);
        h.Db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = h.TenantId,
            ResourceType = null,
            MaxActiveReservationsPerCustomerPerDay = 1,
            MaxAdvanceBookingDays = 1,
            MinimumBookingLeadMinutes = 180,
        });
        await h.Db.SaveChangesAsync();

        // Control: the portal is blocked by the lead time (clock is 07:10, start is 10:00).
        await Assert.ThrowsAsync<BookingLeadTimeNotMetException>(() =>
            Service(h).CreateReservationAsync(h.CustomerA, h.BookRequest(), CancellationToken.None));

        // Lead time: starts inside the 3h minimum.
        await Service(h).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA }, CancellationToken.None);
        // Daily limit: second booking the same day for the same customer.
        await Service(h).CreateStaffReservationAsync(
            StaffRequest(h) with
            {
                CustomerId = h.CustomerA,
                StartTime = new TimeOnly(12, 0),
                EndTime = new TimeOnly(13, 0),
            },
            CancellationToken.None);
        // Horizon: four weeks ahead (same weekday, so pricing exists) with a 1-day horizon.
        await Service(h).CreateStaffReservationAsync(
            StaffRequest(h) with
            {
                CustomerId = h.CustomerA,
                Date = ReservationQueueHarness.BookDate.AddDays(28),
            },
            CancellationToken.None);

        Assert.Equal(3, await h.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Unit_is_taken_from_the_asset_when_omitted_and_checked_when_given()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);

        var inferred = await Service(h).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA, UnitId = null }, CancellationToken.None);
        Assert.Equal(h.UnitId, inferred.UnitId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Service(h).CreateStaffReservationAsync(
                StaffRequest(h) with
                {
                    CustomerId = h.CustomerA,
                    UnitId = Guid.NewGuid(),
                    StartTime = new TimeOnly(15, 0),
                    EndTime = new TimeOnly(16, 0),
                },
                CancellationToken.None));
    }

    private static ReservationService Service(ReservationQueueHarness h, RecordingEvents? events = null) =>
        new(
            h.Db,
            h.TenantProvider,
            new FakeTrialGuard(),
            h.Queue(),
            events ?? new RecordingEvents(),
            SilentRentalsNotifications.Scheduler,
            h.Time);

    private static StaffCreateReservationRequestDto StaffRequest(ReservationQueueHarness h) =>
        new()
        {
            UnitId = h.UnitId,
            Date = ReservationQueueHarness.BookDate,
            StartTime = ReservationQueueHarness.BookStart,
            EndTime = ReservationQueueHarness.BookEnd,
            Items = [new CreateReservationItemRequestDto { AssetId = h.AssetId, Quantity = 1 }],
        };

    private sealed class RecordingEvents : IDomainEventDispatcher<ReservationEvent>
    {
        public List<string> Types { get; } = [];

        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> DispatchAsync(
            ReservationEvent domainEvent,
            CancellationToken cancellationToken = default)
        {
            Types.Add(domainEvent.EventType);
            return Task.FromResult<IReadOnlyList<Guid>>([]);
        }
    }
}
