using Platform.Core.Infrastructure.MigrationOps;

namespace Platform.Api.Tests.MigrationOps;

public sealed class MigrationInventoryTests
{
    [Fact]
    public void Pending_is_assembly_minus_applied_preserving_assembly_order()
    {
        var assembly = new[]
        {
            "20260709145229_InitialCore",
            "20260822144537_AddReservationWaitingQueue",
            "20260827184023_AddTenantRbacV1"
        };
        var applied = new[]
        {
            "20260709145229_InitialCore",
            "20260822144537_AddReservationWaitingQueue"
        };

        var pending = MigrationInventory.ComputePending(assembly, applied);

        Assert.Equal(new[] { "20260827184023_AddTenantRbacV1" }, pending);
    }

    [Fact]
    public void Pending_is_empty_when_all_assembly_migrations_are_applied()
    {
        var migrations = new[] { "A", "B" };

        var pending = MigrationInventory.ComputePending(migrations, migrations);

        Assert.Empty(pending);
    }

    [Fact]
    public void Last_applied_is_null_when_history_is_empty()
    {
        Assert.Null(MigrationInventory.LastApplied([]));
    }
}
