using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// L3: only a real concurrency or unique-key conflict maps to <c>planning.version_conflict</c>.
/// Plus the pure blocking rule and lifecycle of <see cref="AssetAllocationRules"/>.
/// </summary>
public sealed class PlanningPersistenceAndRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Concurrency_exception_maps_to_version_conflict()
    {
        var mapped = PlanningPersistenceErrors.Translate(new DbUpdateConcurrencyException("stale"), Guid.NewGuid());

        Assert.NotNull(mapped);
        Assert.Equal(409, mapped.StatusCode);
        Assert.Equal(PlanningException.VersionConflictCode, mapped.Code);
    }

    [Fact]
    public void Unique_violation_maps_to_version_conflict()
    {
        var exception = new DbUpdateException("save failed", Postgres("23505"));

        var mapped = PlanningPersistenceErrors.Translate(exception, Guid.NewGuid());

        Assert.NotNull(mapped);
        Assert.Equal(PlanningException.VersionConflictCode, mapped.Code);
    }

    [Fact]
    public void Foreign_key_violation_maps_to_asset_not_found_not_to_a_version_conflict()
    {
        var assetId = Guid.NewGuid();
        var exception = new DbUpdateException("save failed", Postgres("23503"));

        var mapped = PlanningPersistenceErrors.Translate(exception, assetId);

        Assert.NotNull(mapped);
        Assert.Equal(404, mapped.StatusCode);
        Assert.Equal(PlanningException.AssetNotFoundCode, mapped.Code);
    }

    [Theory]
    [InlineData("23514")] // check violation
    [InlineData("40001")] // serialization failure
    [InlineData("53300")] // too many connections
    public void Any_other_database_error_is_not_translated(string sqlState)
    {
        var exception = new DbUpdateException("save failed", Postgres(sqlState));

        Assert.Null(PlanningPersistenceErrors.Translate(exception, Guid.NewGuid()));
    }

    [Fact]
    public void A_db_update_exception_without_a_postgres_cause_and_other_exceptions_are_not_translated()
    {
        Assert.Null(PlanningPersistenceErrors.Translate(new DbUpdateException("no cause"), Guid.NewGuid()));
        Assert.Null(PlanningPersistenceErrors.Translate(new InvalidOperationException("boom"), Guid.NewGuid()));
        Assert.Null(PlanningPersistenceErrors.Translate(new TimeoutException(), Guid.NewGuid()));
    }

    [Fact]
    public void Postgres_cause_is_found_through_nested_inner_exceptions()
    {
        var exception = new DbUpdateException("save failed", new InvalidOperationException("wrap", Postgres("23505")));

        Assert.NotNull(PlanningPersistenceErrors.Translate(exception, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(AllocationStatus.Scheduled, 0, true)]
    [InlineData(AllocationStatus.Scheduled, 60, true)]
    [InlineData(AllocationStatus.Running, 0, true)]
    [InlineData(AllocationStatus.Cleaning, 0, true)]
    [InlineData(AllocationStatus.Released, 0, false)]
    [InlineData(AllocationStatus.Cancelled, 0, false)]
    public void Blocking_rule_per_status_before_the_end(AllocationStatus status, int minutesBeforeEnd, bool expected)
    {
        var allocation = Allocation(status, end: Now.AddMinutes(minutesBeforeEnd + 1));

        Assert.Equal(expected, AssetAllocationRules.IsBlocking(allocation, Now));
    }

    [Fact]
    public void Scheduled_manual_allocation_expires_exactly_at_its_occupied_end()
    {
        var allocation = Allocation(AllocationStatus.Scheduled, end: Now);

        Assert.True(AssetAllocationRules.IsExpired(allocation, Now));
        Assert.False(AssetAllocationRules.IsBlocking(allocation, Now));
        Assert.False(AssetAllocationRules.IsExpired(allocation, Now.AddTicks(-1)));
        Assert.True(AssetAllocationRules.IsBlocking(allocation, Now.AddTicks(-1)));
    }

    [Fact]
    public void Only_manual_scheduled_allocations_expire()
    {
        Assert.False(AssetAllocationRules.IsExpired(Allocation(AllocationStatus.Running, end: Now.AddDays(-1)), Now));
        Assert.False(AssetAllocationRules.IsExpired(
            Allocation(AllocationStatus.Scheduled, end: Now.AddDays(-1), source: AssetAllocationSource.CatalogOrder), Now));
        Assert.False(AssetAllocationRules.IsExpired(
            Allocation(AllocationStatus.Scheduled, end: Now.AddDays(-1), source: AssetAllocationSource.WorkOrder), Now));
    }

    [Fact]
    public void Started_allocation_has_no_implicit_end_and_is_overdue_after_its_planned_end()
    {
        var running = Allocation(AllocationStatus.Running, end: Now.AddDays(-30));

        Assert.Equal(DateTimeOffset.MaxValue, AssetAllocationRules.BlockingEnd(running));
        Assert.True(AssetAllocationRules.IsOverdue(running, Now));
        Assert.False(AssetAllocationRules.IsOverdue(Allocation(AllocationStatus.Running, end: Now.AddHours(1)), Now));
        Assert.False(AssetAllocationRules.IsOverdue(Allocation(AllocationStatus.Scheduled, end: Now.AddDays(-1)), Now));
        Assert.Equal(Now.AddHours(1), AssetAllocationRules.BlockingEnd(Allocation(AllocationStatus.Scheduled, end: Now.AddHours(1))));
    }

    [Fact]
    public void Transitions_stamp_the_actuals_and_follow_the_order()
    {
        var allocation = Allocation(AllocationStatus.Scheduled, end: Now.AddHours(1));

        AssetAllocationRules.Start(allocation, Now);
        Assert.Equal(AllocationStatus.Running, allocation.Status);
        Assert.Equal(Now, allocation.ActualStartAt);

        AssetAllocationRules.WorkFinished(allocation, Now.AddMinutes(1));
        Assert.Equal(AllocationStatus.Cleaning, allocation.Status);
        Assert.Equal(Now.AddMinutes(1), allocation.ActualWorkFinishedAt);

        AssetAllocationRules.Release(allocation, Now.AddMinutes(2));
        Assert.Equal(AllocationStatus.Released, allocation.Status);
        Assert.Equal(Now.AddMinutes(2), allocation.ActualReleaseAt);
    }

    [Fact]
    public void Cancel_records_the_reason_and_only_applies_to_scheduled()
    {
        var allocation = Allocation(AllocationStatus.Scheduled, end: Now.AddHours(1));
        AssetAllocationRules.Cancel(allocation, Now, "no longer needed");

        Assert.Equal(AllocationStatus.Cancelled, allocation.Status);
        Assert.Equal("no longer needed", allocation.CancelReason);
        Assert.Equal(Now, allocation.ActualReleaseAt);
        Assert.Throws<PlanningException>(() => AssetAllocationRules.Cancel(allocation, Now, null));
    }

    private static PostgresException Postgres(string sqlState) =>
        new("database error", "ERROR", "ERROR", sqlState);

    private static AssetAllocation Allocation(
        AllocationStatus status,
        DateTimeOffset end,
        AssetAllocationSource source = AssetAllocationSource.Manual) =>
        new()
        {
            TenantId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            Title = "Job",
            PlannedWorkStart = end.AddHours(-1),
            PlannedWorkEnd = end,
            OccupiedStart = end.AddHours(-1),
            OccupiedEnd = end,
            TimeZoneId = "America/Sao_Paulo",
            Status = status,
            Source = source,
        };
}
