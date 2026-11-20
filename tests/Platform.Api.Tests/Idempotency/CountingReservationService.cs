using System.Collections.Concurrent;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Idempotency;

/// <summary>
/// Stands in for the reservation create: counts the creates, can be slow or fail once, and lists
/// the customer's own bookings (so the default <c>GetMineAsync</c> replay path is exercised).
/// </summary>
internal sealed class CountingReservationService : IReservationService
{
    private readonly ConcurrentDictionary<Guid, ReservationResponseDto> store = new();
    private int createCalls;

    public int CreateCalls => Volatile.Read(ref createCalls);

    public bool FailNext { get; set; }

    public TimeSpan Delay { get; set; }

    public async Task<ReservationResponseDto> CreateReservationAsync(
        Guid customerId,
        CreateReservationRequestDto request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref createCalls);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("The slot is no longer available.");
        }

        var start = new DateTimeOffset(request.Date.ToDateTime(request.StartTime), TimeSpan.Zero);
        var reservation = new ReservationResponseDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            request.UnitId,
            customerId,
            "Customer",
            "+5511999990000",
            start,
            start.AddHours(1),
            ReservationStatus.Confirmed,
            50m,
            0m,
            DateTimeOffset.UtcNow,
            []);
        store[reservation.Id] = reservation;
        return reservation;
    }

    public Task<IReadOnlyList<ReservationResponseDto>> ListMineAsync(
        Guid customerId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReservationResponseDto>>(
            store.Values.Where(r => r.CustomerId == customerId).ToList());

    public Task<CheckAvailabilityResponseDto> CheckAvailabilityAsync(
        CheckAvailabilityRequestDto request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<ReservationResponseDto> CreateStaffReservationAsync(
        StaffCreateReservationRequestDto request,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ReservationResponseDto>> ListAdminAsync(
        DateOnly? from,
        DateOnly? to,
        ReservationStatus? status,
        Guid? assetId,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<ReservationResponseDto> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<ReservationResponseDto> CompleteAsync(Guid reservationId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<ReservationResponseDto> CancelAsync(Guid reservationId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<ReservationResponseDto> UpdateNotesAsync(
        Guid reservationId,
        string? notes,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<ReservationResponseDto> CancelByCustomerAsync(
        Guid customerId,
        Guid reservationId,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
