using Platform.Core.Infrastructure.MigrationOps;

namespace Platform.Api.Tests.MigrationOps;

public sealed class PmocPeriodCollisionDiagnosticsTests
{
    [Fact]
    public void Aggregate_counts_duplicate_plan_asset_period_groups()
    {
        var tenant = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var date = new DateOnly(2026, 9, 18);
        var older = new Guid("11111111-1111-1111-1111-111111111111");
        var newer = new Guid("22222222-2222-2222-2222-222222222222");
        var created = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        var counts = PmocPeriodCollisionDiagnostics.Aggregate(
        [
            new(tenant, plan, asset, date, older, created),
            new(tenant, plan, asset, date, newer, created.AddMinutes(1)),
            new(tenant, plan, Guid.NewGuid(), date, Guid.NewGuid(), created),
        ]);

        Assert.Equal(1, counts.CollisionGroups);
        Assert.Equal([older, newer], counts.Samples[0].WorkOrderIds);
        Assert.Equal(asset, counts.Samples[0].AssetId);
    }

    [Fact]
    public void Aggregate_ignores_unique_periods()
    {
        var tenant = Guid.NewGuid();
        var plan = Guid.NewGuid();
        var created = DateTimeOffset.UtcNow;

        var counts = PmocPeriodCollisionDiagnostics.Aggregate(
        [
            new(tenant, plan, Guid.NewGuid(), new DateOnly(2026, 9, 18), Guid.NewGuid(), created),
            new(tenant, plan, Guid.NewGuid(), new DateOnly(2026, 9, 18), Guid.NewGuid(), created),
        ]);

        Assert.Equal(0, counts.CollisionGroups);
        Assert.Empty(counts.Samples);
    }
}
