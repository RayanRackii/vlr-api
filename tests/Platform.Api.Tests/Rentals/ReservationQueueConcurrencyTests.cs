using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationQueueConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public ReservationQueueConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Ten_concurrent_joins_get_unique_sequences()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 10));

        await using var seedDb = factory.Create(tenantProvider);
        var seed = await ReservationQueueHarness.CreateOnAsync(
            seedDb,
            tenantProvider,
            time,
            customerCount: 10);

        var captured = await Task.WhenAll(seed.CustomerIds.Select(async customerId =>
        {
            await using var db = factory.Create(tenantProvider);
            var queue = TestReservationQueue.Create(db, tenantProvider, time);
            return await queue.JoinAsync(seed.RentalAssetId, customerId, CancellationToken.None);
        }));

        var sequences = captured.Select(s => s.MyTicket!.Sequence).OrderBy(s => s).ToList();
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), sequences);
        Assert.Equal(10, sequences.Distinct().Count());

        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(10, await verify.ReservationQueueTickets.CountAsync());
    }

    [DockerFact]
    public async Task Two_concurrent_promotions_leave_exactly_one_Active()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 10));

        await using var seedDb = factory.Create(tenantProvider);
        var seed = await ReservationQueueHarness.CreateOnAsync(
            seedDb,
            tenantProvider,
            time,
            customerCount: 2);

        var queue = seed.Queue();
        await queue.JoinAsync(seed.RentalAssetId, seed.CustomerA, CancellationToken.None);
        await queue.JoinAsync(seed.RentalAssetId, seed.CustomerB, CancellationToken.None);

        time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));

        await Task.WhenAll(
            PromoteAsync(factory, tenantProvider, time, seed.RentalAssetId, seed.CustomerA),
            PromoteAsync(factory, tenantProvider, time, seed.RentalAssetId, seed.CustomerB));

        await using var verify = factory.Create(tenantProvider);
        var tickets = await verify.ReservationQueueTickets.ToListAsync();
        Assert.Equal(1, tickets.Count(t => t.Status == QueueTicketStatus.Active));
        Assert.Equal(1, tickets.Count(t => t.Status == QueueTicketStatus.Waiting));
        Assert.Equal(QueueTicketStatus.Active, tickets.Single(t => t.Sequence == 1).Status);
    }

    [DockerFact]
    public async Task Same_customer_two_concurrent_joins_create_one_ticket()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 10));

        await using var seedDb = factory.Create(tenantProvider);
        var seed = await ReservationQueueHarness.CreateOnAsync(seedDb, tenantProvider, time);

        var results = await Task.WhenAll(
            JoinAsync(factory, tenantProvider, time, seed.RentalAssetId, seed.CustomerA),
            JoinAsync(factory, tenantProvider, time, seed.RentalAssetId, seed.CustomerA));

        Assert.Equal(results[0].MyTicket!.Id, results[1].MyTicket!.Id);

        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await verify.ReservationQueueTickets.CountAsync());
    }

    [DockerFact]
    public async Task Active_timeout_activates_exactly_the_next_Waiting()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 10));

        await using var seedDb = factory.Create(tenantProvider);
        var seed = await ReservationQueueHarness.CreateOnAsync(
            seedDb,
            tenantProvider,
            time,
            customerCount: 3);

        var queue = seed.Queue();
        await queue.JoinAsync(seed.RentalAssetId, seed.CustomerA, CancellationToken.None);
        await queue.JoinAsync(seed.RentalAssetId, seed.CustomerB, CancellationToken.None);
        await queue.JoinAsync(seed.RentalAssetId, seed.CustomerC, CancellationToken.None);

        time.SetUtcNow(ReservationQueueHarness.Brazil(7, 31));
        var active = await queue.GetStatusAsync(seed.RentalAssetId, seed.CustomerA, CancellationToken.None);
        Assert.Equal(QueueTicketStatus.Active, active.MyTicket!.Status);

        time.SetUtcNow(active.MyTicket.TurnExpiresAt!.Value.AddSeconds(1));
        await queue.GetStatusAsync(seed.RentalAssetId, seed.CustomerA, CancellationToken.None);

        await using var verify = factory.Create(tenantProvider);
        var tickets = await verify.ReservationQueueTickets.OrderBy(t => t.Sequence).ToListAsync();
        Assert.Equal(QueueTicketStatus.Expired, tickets[0].Status);
        Assert.Equal(QueueTicketStatus.Active, tickets[1].Status);
        Assert.Equal(QueueTicketStatus.Waiting, tickets[2].Status);
        Assert.Equal(1, tickets.Count(t => t.Status == QueueTicketStatus.Active));
    }

    [DockerFact]
    public async Task BookSlot_and_CreateReservation_bypass_without_Active_are_blocked()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(7, 31));

        await using var seedDb = factory.Create(tenantProvider);
        var seed = await ReservationQueueHarness.CreateOnAsync(
            seedDb,
            tenantProvider,
            time,
            includeSlot: true);

        await using var db = factory.Create(tenantProvider);
        var reservations = new ReservationService(
            db,
            tenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider, time));
        var create = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reservations.CreateReservationAsync(
                seed.CustomerA,
                seed.BookRequest(),
                CancellationToken.None));
        Assert.Equal(ReservationQueueCodes.Required, create.Message);

        var schedule = new ScheduleService(
            db,
            tenantProvider,
            new UnusedOccupancyKindService(),
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider, time));
        var book = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            schedule.BookSlotAsync(
                seed.CustomerA,
                new BookSlotRequestDto
                {
                    SlotId = seed.SlotId!.Value,
                    UnitId = seed.UnitId,
                    Quantity = 1
                },
                CancellationToken.None));
        Assert.Equal(ReservationQueueCodes.Required, book.Message);
    }

    [DockerFact]
    public async Task Queue_disabled_create_still_works_on_postgres()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var time = new TestTimeProvider(ReservationQueueHarness.Brazil(6, 0));

        await using var seedDb = factory.Create(tenantProvider);
        var seed = await ReservationQueueHarness.CreateOnAsync(
            seedDb,
            tenantProvider,
            time,
            queueEnabled: false);

        await using var db = factory.Create(tenantProvider);
        var reservations = new ReservationService(
            db,
            tenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider, time));
        var created = await reservations.CreateReservationAsync(
            seed.CustomerA,
            seed.BookRequest(),
            CancellationToken.None);
        Assert.NotEqual(Guid.Empty, created.Id);
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static async Task<ReservationQueueStatusDto> JoinAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        TestTimeProvider time,
        Guid rentalAssetId,
        Guid customerId)
    {
        await using var db = factory.Create(tenantProvider);
        var queue = TestReservationQueue.Create(db, tenantProvider, time);
        return await queue.JoinAsync(rentalAssetId, customerId, CancellationToken.None);
    }

    private static async Task PromoteAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        TestTimeProvider time,
        Guid rentalAssetId,
        Guid customerId)
    {
        await using var db = factory.Create(tenantProvider);
        var queue = TestReservationQueue.Create(db, tenantProvider, time);
        await queue.GetStatusAsync(rentalAssetId, customerId, CancellationToken.None);
    }

    private sealed class UnusedOccupancyKindService : IOccupancyKindService
    {
        public Task<IReadOnlyList<OccupancyKindResponseDto>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OccupancyKindResponseDto> CreateAsync(
            UpsertOccupancyKindRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OccupancyKindResponseDto> UpdateAsync(
            Guid id,
            UpsertOccupancyKindRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnsureDefaultsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
