using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.ResourcePlanning;

public sealed class InternalUseExclusionTests
{
    [Fact]
    public async Task ListMineAsync_excludes_internal_use_reservations()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var customerBooking = harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        var internalUse = harness.SeedOverlappingReservation(
            ReservationStatus.Confirmed,
            LocationBookingHarness.RangeStart.AddHours(2),
            LocationBookingHarness.RangeEnd.AddHours(2));
        internalUse.Purpose = ReservationPurpose.InternalUse;
        internalUse.CustomerId = harness.CustomerId;
        await harness.Db.SaveChangesAsync();

        var results = await harness.CreateReservationService()
            .ListMineAsync(harness.CustomerId, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(customerBooking.Id, result.Id);
    }

    [Fact]
    public async Task CancelByCustomerAsync_on_internal_use_reservation_is_not_found()
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var internalUse = harness.SeedOverlappingReservation(ReservationStatus.Confirmed);
        internalUse.Purpose = ReservationPurpose.InternalUse;
        internalUse.CustomerId = harness.CustomerId;
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.CreateReservationService()
                .CancelByCustomerAsync(harness.CustomerId, internalUse.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData(nameof(ReservationService.ConfirmAsync))]
    [InlineData(nameof(ReservationService.CompleteAsync))]
    [InlineData(nameof(ReservationService.CancelAsync))]
    public async Task Staff_mutations_on_internal_use_reservation_throw(string method)
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        var internalUse = harness.SeedOverlappingReservation(ReservationStatus.PendingDeposit);
        internalUse.Purpose = ReservationPurpose.InternalUse;
        internalUse.CustomerId = null;
        await harness.Db.SaveChangesAsync();

        var service = harness.CreateReservationService();
        Task Act() => method switch
        {
            nameof(ReservationService.ConfirmAsync) =>
                service.ConfirmAsync(internalUse.Id, CancellationToken.None),
            nameof(ReservationService.CompleteAsync) =>
                service.CompleteAsync(internalUse.Id, CancellationToken.None),
            nameof(ReservationService.CancelAsync) =>
                service.CancelAsync(internalUse.Id, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

        await Assert.ThrowsAsync<InternalUseRequiresOrderWorkflowException>(Act);
    }
}
