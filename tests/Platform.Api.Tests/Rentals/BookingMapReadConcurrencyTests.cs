using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingMapReadConcurrencyTests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task Admin_and_public_reads_keep_metadata_items_and_decorations_on_one_snapshot_during_save()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        foreach (var publicRead in new[] { false, true })
        {
            var tenant = new Tenant("Map snapshot", Guid.NewGuid().ToString("N")[..20]);
            var provider = new FakeTenantProvider { TenantId = tenant.Id };
            BookingMapCompositionDto initial;
            Guid resourceId;
            await using (var seed = factory.Create(provider))
            {
                var unit = new Unit(tenant.Id, "Site");
                var family = new AssetFamily { Key = Guid.NewGuid().ToString("N"), Label = "Resources", FieldSchemaJson = "{}" };
                var category = new AssetCategory { TenantId = tenant.Id, Name = "Resources" };
                var asset = new Asset { TenantId = tenant.Id, UnitId = unit.Id, CategoryId = category.Id,
                    FamilyId = family.Id, Name = "Resource", Tag = "R", Status = AssetStatus.Active, IsRentable = true };
                var rental = new RentalAsset { TenantId = tenant.Id, AssetId = asset.Id, Type = RentalAssetType.Location,
                    TotalQuantity = 1, IsActive = true, RequiresDeposit = false, SchedulePolicy = SchedulePolicy.OpenHours };
                seed.AddRange(tenant, unit, family, category, asset, rental);
                await seed.SaveChangesAsync();
                resourceId = rental.Id;
                var manifest = new SeedDemoBookingMapRequest { UnitId = unit.Id, Name = "Version one",
                    Items = [new() { RentalAssetId = resourceId, XPercent = 5, YPercent = 10, WidthPercent = 30, HeightPercent = 40 }],
                    Decorations = [BookingMapTests.Decoration() with { Label = "Version one" }] };
                initial = await Service(seed, provider).SeedDemoAsync(manifest, default);
                // Both new and idempotent seed responses must close their write transaction before reading.
                Assert.Equal(initial.Id, (await Service(seed, provider).SeedDemoAsync(manifest, default)).Id);
                Assert.Null(seed.Database.CurrentTransaction);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pause = new PauseBeforeItems();
            await using var reader = factory.Create(provider, pause);
            await using var writer = factory.Create(provider);
            var read = ReadAsync(Service(reader, provider), initial.Id, publicRead, timeout.Token);
            try
            {
                await pause.Started.Task.WaitAsync(timeout.Token);
                Assert.False(read.IsCompleted);
                var request = BookingMapTests.Composition(initial) with { Name = "Version two",
                    Items = [BookingMapTests.Composition(initial).Items[0] with { XPercent = 45 }],
                    Decorations = [BookingMapTests.Decoration() with { Label = "Version two" }] };
                var saved = await Service(writer, provider).SaveCompositionAsync(initial.Id, request, timeout.Token);
                Assert.Equal(initial.Version + 1, saved.Version);
                Assert.Null(writer.Database.CurrentTransaction);
            }
            finally { pause.Resume.TrySetResult(); }

            var snapshot = await read.WaitAsync(timeout.Token);
            Assert.Equal(initial.Version, snapshot.Version);
            Assert.Equal("Version one", snapshot.Name);
            Assert.Equal(resourceId, Assert.Single(snapshot.Items).RentalAssetId);
            Assert.Equal(5, snapshot.Items[0].XPercent);
            Assert.Equal("Version one", Assert.Single(snapshot.Decorations).Label);
            Assert.Null(reader.Database.CurrentTransaction);

            var latest = await ReadAsync(Service(reader, provider), initial.Id, publicRead, timeout.Token);
            Assert.Equal(initial.Version + 1, latest.Version);
            Assert.Equal("Version two", latest.Name);
            Assert.Equal(45, Assert.Single(latest.Items).XPercent);
            Assert.Equal("Version two", Assert.Single(latest.Decorations).Label);
        }
    }

    private static RentalLayoutService Service(AppDbContext db, FakeTenantProvider provider) => new(db, provider, new FakeTrialGuard());

    private static async Task<Snapshot> ReadAsync(RentalLayoutService service, Guid id, bool publicRead, CancellationToken ct)
    {
        if (publicRead)
        {
            var map = await service.GetPublicMapAsync(id, null, ct);
            Assert.NotNull(map);
            return new(map.Version, map.Name, map.Items, map.Decorations);
        }
        var composition = await service.GetCompositionAsync(id, ct);
        Assert.NotNull(composition);
        return new(composition.Version, composition.Name, composition.Items, composition.Decorations);
    }

    private sealed record Snapshot(long Version, string Name, IReadOnlyList<BookingMapItemDto> Items,
        IReadOnlyList<BookingMapDecorationDto> Decorations);

    private sealed class PauseBeforeItems : DbCommandInterceptor
    {
        private bool paused;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // The metadata reader has already completed; the writer commits before the next SELECT.
            if (!paused && command.CommandText.Contains("FROM rentals.layout_items", StringComparison.Ordinal))
            {
                paused = true;
                Started.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
