using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationLifecycleTests
{
    [Fact]
    public async Task ConfirmAsync_pending_deposit_becomes_confirmed()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.PendingDeposit);
        await harness.Db.SaveChangesAsync();

        var result = await harness.CreateReservationService()
            .ConfirmAsync(reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Confirmed, result.Status);
        Assert.Equal(
            ReservationStatus.Confirmed,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task ConfirmAsync_confirmed_is_idempotent()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        await harness.Db.SaveChangesAsync();

        var result = await harness.CreateReservationService()
            .ConfirmAsync(reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Confirmed, result.Status);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task ConfirmAsync_canceled_throws()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Canceled);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateReservationService().ConfirmAsync(reservation.Id, CancellationToken.None));

        Assert.Contains(nameof(ReservationStatus.Canceled), ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            ReservationStatus.Canceled,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task CompleteAsync_confirmed_becomes_completed()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        await harness.Db.SaveChangesAsync();

        var result = await harness.CreateReservationService()
            .CompleteAsync(reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Completed, result.Status);
        Assert.Equal(
            ReservationStatus.Completed,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task CompleteAsync_completed_is_idempotent()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Completed);
        await harness.Db.SaveChangesAsync();

        var result = await harness.CreateReservationService()
            .CompleteAsync(reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Completed, result.Status);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task CompleteAsync_pending_deposit_throws()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.PendingDeposit);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateReservationService().CompleteAsync(reservation.Id, CancellationToken.None));

        Assert.Contains(nameof(ReservationStatus.PendingDeposit), ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            ReservationStatus.PendingDeposit,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task CompleteAsync_canceled_throws()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Canceled);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateReservationService().CompleteAsync(reservation.Id, CancellationToken.None));

        Assert.Contains(nameof(ReservationStatus.Canceled), ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            ReservationStatus.Canceled,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    [Fact]
    public async Task CompleteAsync_missing_throws_not_found()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.CreateReservationService().CompleteAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task CompleteAsync_does_not_release_linked_slot()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        var kind = new OccupancyKind
        {
            TenantId = harness.TenantId,
            Key = "open",
            Label = "Aberto",
            IsBookableByCustomer = true,
            BlocksCapacity = true,
            SortOrder = 0,
            IsActive = true,
        };
        var slot = new Slot
        {
            TenantId = harness.TenantId,
            RentalAssetId = harness.RentalAssetId,
            Date = LocationBookingHarness.Date,
            StartTime = LocationBookingHarness.Start,
            EndTime = LocationBookingHarness.End,
            OccupancyKindId = kind.Id,
            Status = SlotStatus.Booked,
            ReservationId = reservation.Id,
        };
        harness.Db.OccupancyKinds.Add(kind);
        harness.Db.Slots.Add(slot);
        await harness.Db.SaveChangesAsync();

        await harness.CreateReservationService().CompleteAsync(reservation.Id, CancellationToken.None);

        var persisted = await harness.Db.Slots.SingleAsync(s => s.Id == slot.Id);
        Assert.Equal(SlotStatus.Booked, persisted.Status);
        Assert.Equal(reservation.Id, persisted.ReservationId);
    }

    [Fact]
    public async Task CompleteAsync_other_tenant_reservation_throws_not_found()
    {
        var databaseName = $"complete-iso-{Guid.NewGuid():N}";
        var providerA = new FakeTenantProvider();
        var providerB = new FakeTenantProvider();

        Guid reservationAId;
        await using (var dbA = InMemoryAppDb.Create(providerA, databaseName))
        {
            var tenantA = new Tenant("Club A", UniqueTaxId(), subdomain: $"a-{Guid.NewGuid():N}"[..20]);
            var unit = new Unit(tenantA.Id, "Matriz");
            var category = new AssetCategory { TenantId = tenantA.Id, Name = "Quadras" };
            var family = new AssetFamily
            {
                Key = $"spaces-{Guid.NewGuid():N}"[..32],
                Label = "Spaces",
                FieldSchemaJson = "{}",
            };
            var asset = new Asset
            {
                TenantId = tenantA.Id,
                UnitId = unit.Id,
                CategoryId = category.Id,
                FamilyId = family.Id,
                Name = "Quadra 1",
                Tag = "Q1",
                Status = AssetStatus.Active,
                IsRentable = true,
            };
            var rental = new RentalAsset
            {
                TenantId = tenantA.Id,
                AssetId = asset.Id,
                Type = RentalAssetType.Location,
                TotalQuantity = 1,
                IsActive = true,
                RequiresDeposit = true,
                SchedulePolicy = SchedulePolicy.OpenHours,
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(22, 0),
                QueueEnabled = false,
            };
            var customer = new Customer
            {
                TenantId = tenantA.Id,
                Name = "Ana",
                Email = "ana@club.test",
            };
            var reservation = new Reservation
            {
                TenantId = tenantA.Id,
                UnitId = unit.Id,
                CustomerId = customer.Id,
                CustomerName = "Ana",
                CustomerWhatsApp = "11999999999",
                StartDateTime = LocationBookingHarness.RangeStart,
                EndDateTime = LocationBookingHarness.RangeEnd,
                Status = ReservationStatus.Confirmed,
                TotalAmount = 100m,
                DepositPaid = 0m,
            };
            reservation.AddItem(new ReservationItem
            {
                TenantId = tenantA.Id,
                ReservationId = reservation.Id,
                RentalAssetId = rental.Id,
                Quantity = 1,
                UnitPrice = 100m,
                SubTotal = 100m,
            });

            providerA.TenantId = tenantA.Id;
            dbA.Tenants.Add(tenantA);
            dbA.Units.Add(unit);
            dbA.AssetCategories.Add(category);
            dbA.AssetFamilies.Add(family);
            dbA.Assets.Add(asset);
            dbA.RentalAssets.Add(rental);
            dbA.Customers.Add(customer);
            dbA.Reservations.Add(reservation);
            await dbA.SaveChangesAsync();
            reservationAId = reservation.Id;
        }

        await using var dbB = InMemoryAppDb.Create(providerB, databaseName);
        var tenantB = new Tenant("Club B", UniqueTaxId(), subdomain: $"b-{Guid.NewGuid():N}"[..20]);
        providerB.TenantId = tenantB.Id;
        dbB.Tenants.Add(tenantB);
        await dbB.SaveChangesAsync();

        var serviceB = new ReservationService(
            dbB,
            providerB,
            new FakeTrialGuard(),
            TestReservationQueue.Create(dbB, providerB),
            SilentRentalsNotifications.Publisher,
            SilentRentalsNotifications.Scheduler);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            serviceB.CompleteAsync(reservationAId, CancellationToken.None));
        Assert.Empty(await dbB.Reservations.ToListAsync());
    }

    [Fact]
    public async Task CancelAsync_already_canceled_is_idempotent()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Canceled);
        await harness.Db.SaveChangesAsync();

        var result = await harness.CreateReservationService()
            .CancelAsync(reservation.Id, CancellationToken.None);

        Assert.Equal(ReservationStatus.Canceled, result.Status);
    }

    [Fact]
    public async Task CancelAsync_completed_throws()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var reservation = harness.SeedOverlappingReservation(ReservationStatus.Completed);
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.CreateReservationService().CancelAsync(reservation.Id, CancellationToken.None));
        Assert.Equal(
            ReservationStatus.Completed,
            await harness.Db.Reservations.Where(r => r.Id == reservation.Id).Select(r => r.Status).SingleAsync());
    }

    private static string UniqueTaxId() => Guid.NewGuid().ToString("N")[..14];
}
