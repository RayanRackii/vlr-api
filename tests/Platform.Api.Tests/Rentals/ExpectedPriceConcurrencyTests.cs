using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class ExpectedPriceConcurrencyTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public Task Create_recomputes_price_after_waiting_for_lock_and_preserves_active_turn() =>
        PriceChangesWhileWaitingAsync(persisted: false);

    [DockerFact]
    public Task BookSlot_recomputes_price_after_waiting_for_lock_and_preserves_active_turn() =>
        PriceChangesWhileWaitingAsync(persisted: true);

    private async Task PriceChangesWhileWaitingAsync(bool persisted)
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 10));
        await using var seedDb = factory.Create(provider);
        var seed = await ReservationQueueHarness.CreateOnAsync(seedDb, provider, time, includeSlot: true);
        var active = await ExpectedPriceTests.ActivateAsync(seed);

        await using var holder = factory.Create(provider);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT 1 FROM rentals.rental_assets
            WHERE id = {seed.RentalAssetId} AND tenant_id = {seed.TenantId} FOR UPDATE
            """);
        (await holder.RentalPricings.SingleAsync()).PricePerHour = 125m;
        await holder.SaveChangesAsync();

        var observedLock = new ObserveAssetLock();
        await using var bookDb = factory.Create(provider, observedLock);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var booking = BookAsync(bookDb, seed, persisted, 100m, timeout.Token);
        await observedLock.Attempted.Task.WaitAsync(timeout.Token);
        Assert.False(booking.IsCompleted);
        await transaction.CommitAsync(timeout.Token);

        var error = await Assert.ThrowsAsync<PriceChangedException>(() => booking);
        Assert.Equal(125m, error.CurrentTotalAmount);
        await using var verify = factory.Create(provider);
        Assert.Empty(await verify.Reservations.ToListAsync());
        Assert.Empty(await verify.ReservationItems.ToListAsync());
        var tickets = await verify.ReservationQueueTickets.OrderBy(t => t.Sequence).ToListAsync();
        Assert.Equal(QueueTicketStatus.Active, tickets[0].Status);
        Assert.Equal(active.MyTicket!.TurnExpiresAt, tickets[0].TurnExpiresAt);
        Assert.Null(tickets[0].CompletedReservationId);
        Assert.Equal(QueueTicketStatus.Waiting, tickets[1].Status);
        Assert.Equal(SlotStatus.Available, (await verify.Slots.SingleAsync()).Status);

        await using var retryDb = factory.Create(provider);
        var created = await BookAsync(retryDb, seed, persisted, 125m, CancellationToken.None);
        Assert.Equal(125m, created.TotalAmount);
        verify.ChangeTracker.Clear();
        Assert.Single(await verify.Reservations.ToListAsync());
        Assert.Equal(QueueTicketStatus.Completed,
            (await verify.ReservationQueueTickets.SingleAsync(t => t.CustomerId == seed.CustomerA)).Status);
    }

    [DockerFact]
    public async Task Guarded_create_and_book_on_same_location_have_one_winner()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 10));
        await using var seedDb = factory.Create(provider);
        var seed = await ReservationQueueHarness.CreateOnAsync(seedDb, provider, time,
            queueEnabled: false, includeSlot: true);
        await using var db1 = factory.Create(provider);
        await using var db2 = factory.Create(provider);
        var results = await Task.WhenAll(
            CaptureAsync(() => BookAsync(db1, seed, false, 100m, CancellationToken.None)),
            CaptureAsync(() => BookAsync(db2, seed, true, 100m, CancellationToken.None)));
        Assert.Single(results, e => e is null);
        Assert.IsAssignableFrom<InvalidOperationException>(Assert.Single(results, e => e is not null));
        await using var verify = factory.Create(provider);
        Assert.Single(await verify.Reservations.ToListAsync());
        Assert.Equal(100m, (await verify.Reservations.SingleAsync()).TotalAmount);
    }

    [DockerFact]
    public async Task Guarded_stock_requests_cannot_exceed_capacity()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 10));
        await using var seedDb = factory.Create(provider);
        var seed = await ReservationQueueHarness.CreateOnAsync(seedDb, provider, time, queueEnabled: false);
        var rental = await seedDb.RentalAssets.SingleAsync();
        rental.Type = RentalAssetType.Good;
        rental.TotalQuantity = 3;
        await seedDb.SaveChangesAsync();
        var request = seed.BookRequest() with
        {
            ExpectedTotalAmount = 200m,
            Items = [new() { AssetId = seed.AssetId, Quantity = 2 }]
        };
        await using var db1 = factory.Create(provider);
        await using var db2 = factory.Create(provider);
        var results = await Task.WhenAll(
            CaptureAsync(() => Reservations(db1, seed).CreateReservationAsync(seed.CustomerA, request, CancellationToken.None)),
            CaptureAsync(() => Reservations(db2, seed).CreateReservationAsync(seed.CustomerB, request, CancellationToken.None)));
        Assert.Single(results, e => e is null);
        Assert.IsAssignableFrom<InvalidOperationException>(Assert.Single(results, e => e is not null));
        await using var verify = factory.Create(provider);
        Assert.Equal(2, await verify.ReservationItems.SumAsync(i => i.Quantity));
        Assert.Equal(200m, (await verify.Reservations.SingleAsync()).TotalAmount);
    }

    [DockerFact]
    public async Task Guarded_requests_still_serialize_customer_daily_limit()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 10));
        await using var seedDb = factory.Create(provider);
        var seed = await ReservationQueueHarness.CreateOnAsync(seedDb, provider, time,
            queueEnabled: false, includeSlot: true);
        seedDb.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = seed.TenantId, MaxActiveReservationsPerCustomerPerDay = 1
        });
        await seedDb.SaveChangesAsync();
        await using var db1 = factory.Create(provider);
        await using var db2 = factory.Create(provider);
        var results = await Task.WhenAll(
            CaptureAsync(() => BookAsync(db1, seed, false, 100m, CancellationToken.None)),
            CaptureAsync(() => BookAsync(db2, seed, true, 100m, CancellationToken.None)));
        Assert.Single(results, e => e is null);
        Assert.IsType<DailyBookingLimitReachedException>(Assert.Single(results, e => e is not null));
        await using var verify = factory.Create(provider);
        Assert.Single(await verify.Reservations.ToListAsync());
    }

    private PostgresAppDbFactory RequireFactory() => Assert.IsType<PostgresAppDbFactory>(postgres.Factory);

    private static ReservationService Reservations(AppDbContext db, ReservationQueueHarness seed) => new(
        db, seed.TenantProvider, new FakeTrialGuard(), TestReservationQueue.Create(db, seed.TenantProvider, seed.Time),
        SilentRentalsNotifications.Publisher, SilentRentalsNotifications.Scheduler, seed.Time);

    private static Task<ReservationResponseDto> BookAsync(AppDbContext db, ReservationQueueHarness seed,
        bool persisted, decimal expected, CancellationToken ct) => persisted
        ? new ScheduleService(db, seed.TenantProvider, new OccupancyKindService(db, seed.TenantProvider),
            new FakeTrialGuard(), TestReservationQueue.Create(db, seed.TenantProvider, seed.Time),
            SilentRentalsNotifications.Publisher, SilentRentalsNotifications.Scheduler, seed.Time)
            .BookSlotAsync(seed.CustomerA, new BookSlotRequestDto
            {
                SlotId = seed.SlotId!.Value, UnitId = seed.UnitId, ExpectedTotalAmount = expected
            }, ct)
        : Reservations(db, seed).CreateReservationAsync(seed.CustomerA,
            seed.BookRequest() with { ExpectedTotalAmount = expected }, ct);

    private static async Task<Exception?> CaptureAsync(Func<Task<ReservationResponseDto>> book)
    {
        try { await book(); return null; }
        catch (InvalidOperationException error) { return error; }
    }

    private sealed class ObserveAssetLock : DbCommandInterceptor
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("rentals.rental_assets", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                Attempted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
