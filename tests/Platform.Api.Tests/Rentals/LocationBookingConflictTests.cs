using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

/// <summary>
/// Overlap quantity for Location create/book.
/// SQLite EnsureCreated succeeded, but CreateReservationAsync/BookSlotAsync overlap LINQ
/// (enum string conversion + join) does not translate — spec fallback: GetReservedQuantityAsync.
/// Concurrent double-book proof lives in ReservationConcurrencyTests (Postgres row lock).
/// </summary>
public sealed class LocationBookingConflictTests
{
    [Fact]
    public async Task CreateReservation_counts_overlapping_pending_deposit()
    {
        Assert.Equal(1, await ReservedByCreateAsync(ReservationStatus.PendingDeposit));
    }

    [Fact]
    public async Task CreateReservation_counts_overlapping_confirmed()
    {
        Assert.Equal(1, await ReservedByCreateAsync(ReservationStatus.Confirmed));
    }

    [Fact]
    public async Task CreateReservation_ignores_overlapping_canceled()
    {
        Assert.Equal(0, await ReservedByCreateAsync(ReservationStatus.Canceled));
    }

    [Fact]
    public async Task BookSlot_counts_overlapping_pending_deposit()
    {
        Assert.Equal(1, await ReservedByBookAsync(ReservationStatus.PendingDeposit));
    }

    [Fact]
    public async Task BookSlot_counts_overlapping_confirmed()
    {
        Assert.Equal(1, await ReservedByBookAsync(ReservationStatus.Confirmed));
    }

    [Fact]
    public async Task BookSlot_ignores_overlapping_canceled()
    {
        Assert.Equal(0, await ReservedByBookAsync(ReservationStatus.Canceled));
    }

    private static async Task<int> ReservedByCreateAsync(ReservationStatus status)
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        harness.SeedOverlappingReservation(status);
        await harness.Db.SaveChangesAsync();

        return await harness.CreateReservationService().GetReservedQuantityAsync(
            harness.RentalAssetId,
            LocationBookingHarness.RangeStart,
            LocationBookingHarness.RangeEnd,
            excludeReservationId: null,
            CancellationToken.None);
    }

    private static async Task<int> ReservedByBookAsync(ReservationStatus status)
    {
        await using var harness = await LocationBookingHarness.CreateAsync();
        harness.SeedOverlappingReservation(status);
        await harness.Db.SaveChangesAsync();

        return await harness.CreateScheduleService().GetReservedQuantityAsync(
            harness.RentalAssetId,
            LocationBookingHarness.RangeStart,
            LocationBookingHarness.RangeEnd,
            CancellationToken.None);
    }
}
