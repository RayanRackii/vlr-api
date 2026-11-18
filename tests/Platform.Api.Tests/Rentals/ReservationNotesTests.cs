using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationNotesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Staff_create_stores_a_trimmed_note_and_returns_it_to_staff()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);

        var created = await StaffService(h).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA, Notes = "  Cliente pediu bola extra  " }, Ct);

        Assert.Equal("Cliente pediu bola extra", created.Notes);
        Assert.Equal("Cliente pediu bola extra", (await h.Db.Reservations.SingleAsync()).Notes);
    }

    [Fact]
    public async Task Staff_create_without_a_note_stores_null_and_omits_it_from_json()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);

        var created = await StaffService(h).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA, Notes = "   " }, Ct);

        Assert.Null(created.Notes);
        Assert.Null((await h.Db.Reservations.SingleAsync()).Notes);
        Assert.DoesNotContain("notes", JsonSerializer.Serialize(created, Web), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Staff_create_rejects_a_note_over_1000_characters_and_creates_nothing()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false, customerCount: 1);

        await Assert.ThrowsAsync<ArgumentException>(() => StaffService(h).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA, Notes = new string('x', Reservation.NotesMaxLength + 1) }, Ct));

        Assert.Empty(await h.Db.Reservations.ToListAsync());

        var exact = await StaffService(h).CreateStaffReservationAsync(
            StaffRequest(h) with { CustomerId = h.CustomerA, Notes = new string('x', Reservation.NotesMaxLength) }, Ct);
        Assert.Equal(Reservation.NotesMaxLength, exact.Notes!.Length);
    }

    [Fact]
    public async Task UpdateNotes_sets_replaces_and_clears_the_note()
    {
        await using var h = await LocationBookingHarness.CreateAsync();
        var reservation = h.SeedOverlappingReservation(ReservationStatus.Confirmed);
        await h.Db.SaveChangesAsync();
        var service = h.CreateReservationService();

        var set = await service.UpdateNotesAsync(reservation.Id, " Alergia a amendoim ", Ct);
        Assert.Equal("Alergia a amendoim", set.Notes);
        h.Db.ChangeTracker.Clear();
        Assert.Equal("Alergia a amendoim", (await h.Db.Reservations.SingleAsync()).Notes);

        var replaced = await service.UpdateNotesAsync(reservation.Id, "Outra", Ct);
        Assert.Equal("Outra", replaced.Notes);

        var cleared = await service.UpdateNotesAsync(reservation.Id, "  ", Ct);
        Assert.Null(cleared.Notes);
        h.Db.ChangeTracker.Clear();
        Assert.Null((await h.Db.Reservations.SingleAsync()).Notes);
    }

    [Fact]
    public async Task UpdateNotes_rejects_a_note_over_1000_characters_and_keeps_the_old_one()
    {
        await using var h = await LocationBookingHarness.CreateAsync();
        var reservation = h.SeedOverlappingReservation(ReservationStatus.Confirmed);
        reservation.Notes = "antiga";
        await h.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => h.CreateReservationService()
            .UpdateNotesAsync(reservation.Id, new string('x', Reservation.NotesMaxLength + 1), Ct));

        h.Db.ChangeTracker.Clear();
        Assert.Equal("antiga", (await h.Db.Reservations.SingleAsync()).Notes);
    }

    [Fact]
    public async Task UpdateNotes_on_a_missing_or_other_tenant_reservation_is_not_found()
    {
        await using var h = await LocationBookingHarness.CreateAsync();
        var foreign = h.SeedOverlappingReservation(ReservationStatus.Confirmed);
        foreign.TenantId = Guid.NewGuid();
        foreign.Notes = "outra empresa";
        await h.Db.SaveChangesAsync();
        var service = h.CreateReservationService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateNotesAsync(Guid.NewGuid(), "x", Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateNotesAsync(foreign.Id, "invadido", Ct));

        h.TenantProvider.TenantId = foreign.TenantId;
        h.Db.ChangeTracker.Clear();
        Assert.Equal("outra empresa", (await h.Db.Reservations.SingleAsync()).Notes);
    }

    [Fact]
    public async Task UpdateNotes_keeps_the_order_workflow_guard_for_internal_use()
    {
        await using var h = await LocationBookingHarness.CreateAsync();
        var reservation = h.SeedOverlappingReservation(ReservationStatus.Confirmed);
        reservation.Purpose = ReservationPurpose.InternalUse;
        reservation.CustomerId = null;
        reservation.CustomerName = null;
        reservation.CustomerWhatsApp = null;
        await h.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InternalUseRequiresOrderWorkflowException>(() => h.CreateReservationService()
            .UpdateNotesAsync(reservation.Id, "nao pode", Ct));

        h.Db.ChangeTracker.Clear();
        Assert.Null((await h.Db.Reservations.SingleAsync()).Notes);
    }

    [Fact]
    public async Task UpdateNotes_is_blocked_while_the_trial_is_read_only()
    {
        await using var h = await LocationBookingHarness.CreateAsync();
        var reservation = h.SeedOverlappingReservation(ReservationStatus.Confirmed);
        await h.Db.SaveChangesAsync();
        var service = new ReservationService(
            h.Db,
            h.TenantProvider,
            new ReadOnlyTrialGuard(),
            TestReservationQueue.Create(h.Db, h.TenantProvider),
            SilentRentalsNotifications.Publisher.AsDomainEvents(),
            SilentRentalsNotifications.Scheduler,
            LocationBookingHarness.BookingClock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateNotesAsync(reservation.Id, "x", Ct));

        Assert.Null((await h.Db.Reservations.SingleAsync()).Notes);
    }

    [Fact]
    public async Task Staff_list_and_actions_return_the_note_but_customer_responses_never_do()
    {
        await using var h = await LocationBookingHarness.CreateAsync();
        var reservation = h.SeedOverlappingReservation(ReservationStatus.PendingDeposit);
        reservation.Notes = "so a equipe ve";
        await h.Db.SaveChangesAsync();
        var service = h.CreateReservationService();

        var listed = Assert.Single(await service.ListAdminAsync(null, null, null, null, Ct));
        Assert.Equal("so a equipe ve", listed.Notes);

        var confirmed = await service.ConfirmAsync(reservation.Id, Ct);
        Assert.Equal("so a equipe ve", confirmed.Notes);

        var mine = Assert.Single(await service.ListMineAsync(h.CustomerId, Ct));
        Assert.Null(mine.Notes);
        Assert.DoesNotContain("notes", JsonSerializer.Serialize(mine, Web), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("so a equipe ve", JsonSerializer.Serialize(mine, Web), StringComparison.Ordinal);

        var staffJson = JsonSerializer.Serialize(listed, Web);
        Assert.Contains("\"notes\":\"so a equipe ve\"", staffJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Customer_cancel_response_does_not_include_the_note()
    {
        await using var h = await LocationBookingHarness.CreateAsync();
        var reservation = h.SeedOverlappingReservation(ReservationStatus.Confirmed);
        reservation.Notes = "interna";
        await h.Db.SaveChangesAsync();

        var canceled = await h.CreateReservationService().CancelByCustomerAsync(h.CustomerId, reservation.Id, Ct);

        Assert.Null(canceled.Notes);
    }

    [Fact]
    public void Notes_endpoint_requires_create_or_confirm_permission()
    {
        var method = typeof(ReservationsController).GetMethod(nameof(ReservationsController.UpdateNotes));
        Assert.NotNull(method);

        var attribute = Assert.Single(method.GetCustomAttributes(typeof(RequireAnyPermissionAttribute), inherit: true))
            as RequireAnyPermissionAttribute;
        Assert.NotNull(attribute);
        Assert.Equal(
            [Permissions.Rentals.ReservationsConfirm, Permissions.Rentals.ReservationsCreate],
            PermissionPolicies.TryParseAny(attribute.Policy!, out var keys)
                ? keys.Order(StringComparer.Ordinal).ToArray()
                : []);
    }

    private static ReservationService StaffService(ReservationQueueHarness h) =>
        new(
            h.Db,
            h.TenantProvider,
            new FakeTrialGuard(),
            h.Queue(),
            SilentRentalsNotifications.Publisher.AsDomainEvents(),
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

    private sealed class ReadOnlyTrialGuard : ITrialGuard
    {
        public Task EnsureWritableAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This trial has ended.");

        public Task EnsureCanCreateAssetsAsync(int additionalCount, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EnsureCanInviteUserAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
