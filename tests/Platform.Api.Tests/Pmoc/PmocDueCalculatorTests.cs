using Platform.Api.Jobs;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Pmoc;

public sealed class PmocDueCalculatorTests
{
    [Fact]
    public void Never_executed_future_first_due_is_not_due()
    {
        var result = Compute(firstDue: new DateOnly(2026, 10, 1), asOf: new DateOnly(2026, 9, 19));

        Assert.Equal(PmocHistoryStatus.NeverExecuted, result.HistoryStatus);
        Assert.Equal(PmocDueStatus.NotDue, result.DueStatus);
        Assert.Equal(new DateOnly(2026, 10, 1), result.NextDueDate);
        Assert.False(result.NeedsAttention);
    }

    [Fact]
    public void Never_executed_today_is_due_today()
    {
        var asOf = new DateOnly(2026, 9, 19);
        var result = Compute(firstDue: asOf, asOf: asOf);

        Assert.Equal(PmocHistoryStatus.NeverExecuted, result.HistoryStatus);
        Assert.Equal(PmocDueStatus.DueToday, result.DueStatus);
        Assert.True(result.NeedsAttention);
    }

    [Fact]
    public void Never_executed_past_first_due_is_overdue()
    {
        var result = Compute(firstDue: new DateOnly(2026, 9, 1), asOf: new DateOnly(2026, 9, 19));

        Assert.Equal(PmocHistoryStatus.NeverExecuted, result.HistoryStatus);
        Assert.Equal(PmocDueStatus.Overdue, result.DueStatus);
        Assert.Equal(new DateOnly(2026, 9, 1), result.NextDueDate);
        Assert.True(result.NeedsAttention);
    }

    [Fact]
    public void Newly_eligible_after_first_due_stays_on_first_due_and_is_overdue()
    {
        var result = Compute(firstDue: new DateOnly(2026, 9, 1), asOf: new DateOnly(2026, 9, 19));

        Assert.Equal(new DateOnly(2026, 9, 1), result.NextDueDate);
        Assert.Equal(PmocDueStatus.Overdue, result.DueStatus);
    }

    [Fact]
    public void Latest_completed_is_selected_by_completed_then_scheduled_then_id()
    {
        var olderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var newerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var last = PmocDueCalculator.PickLastCompleted(
        [
            (olderId, WorkOrderStatus.Completed, new DateOnly(2026, 9, 15), BrazilNoon(2026, 9, 10)),
            (newerId, WorkOrderStatus.Completed, new DateOnly(2026, 9, 1), BrazilNoon(2026, 9, 18)),
            (Guid.NewGuid(), WorkOrderStatus.Pending, new DateOnly(2026, 9, 20), null),
        ]);

        Assert.NotNull(last);
        Assert.Equal(newerId, last!.Id);
        var result = PmocDueCalculator.Compute(
            new PmocDueInput(30, new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 19), last));
        Assert.Equal(new DateOnly(2026, 10, 18), result.NextDueDate);
        Assert.Equal(PmocHistoryStatus.Executed, result.HistoryStatus);
    }

    [Fact]
    public void Brazil_civil_date_is_used_not_utc_calendar_date()
    {
        var completedUtc = new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 12), BrazilTimeZone.GetCivilDate(completedUtc));

        var result = PmocDueCalculator.Compute(
            new PmocDueInput(
                30,
                new DateOnly(2026, 1, 1),
                new DateOnly(2026, 10, 12),
                new PmocLastCompleted(completedUtc, new DateOnly(2026, 10, 20), Guid.NewGuid())));

        Assert.Equal(new DateOnly(2026, 11, 11), result.NextDueDate);
    }

    [Fact]
    public void Early_completion_resets_from_actual_civil_date()
    {
        var result = ComputeExecuted(
            completedCivil: new DateOnly(2026, 10, 12),
            asOf: new DateOnly(2026, 10, 12));

        Assert.Equal(new DateOnly(2026, 11, 11), result.NextDueDate);
        Assert.Equal(PmocDueStatus.NotDue, result.DueStatus);
    }

    [Fact]
    public void Late_completion_does_not_compensate_drift()
    {
        var result = ComputeExecuted(
            completedCivil: new DateOnly(2026, 10, 27),
            asOf: new DateOnly(2026, 10, 27));

        Assert.Equal(new DateOnly(2026, 11, 26), result.NextDueDate);
    }

    [Fact]
    public void Pending_in_progress_canceled_and_unlinked_are_ignored()
    {
        var last = PmocDueCalculator.PickLastCompleted(
        [
            (Guid.NewGuid(), WorkOrderStatus.Pending, new DateOnly(2026, 9, 19), BrazilNoon(2026, 9, 19)),
            (Guid.NewGuid(), WorkOrderStatus.InProgress, new DateOnly(2026, 9, 18), BrazilNoon(2026, 9, 18)),
            (Guid.NewGuid(), WorkOrderStatus.Canceled, new DateOnly(2026, 9, 17), BrazilNoon(2026, 9, 17)),
        ]);

        Assert.Null(last);
    }

    [Fact]
    public void Executed_asset_responds_to_interval_days_edit()
    {
        var last = new PmocLastCompleted(
            BrazilNoon(2026, 9, 10),
            new DateOnly(2026, 9, 10),
            Guid.NewGuid());

        var before = PmocDueCalculator.Compute(
            new PmocDueInput(30, new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 19), last));
        var after = PmocDueCalculator.Compute(
            new PmocDueInput(60, new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 19), last));

        Assert.Equal(new DateOnly(2026, 10, 10), before.NextDueDate);
        Assert.Equal(new DateOnly(2026, 11, 9), after.NextDueDate);
    }

    [Fact]
    public void Executed_asset_ignores_first_due_date_edit()
    {
        var last = new PmocLastCompleted(
            BrazilNoon(2026, 9, 10),
            new DateOnly(2026, 9, 10),
            Guid.NewGuid());

        var left = PmocDueCalculator.Compute(
            new PmocDueInput(30, new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 19), last));
        var right = PmocDueCalculator.Compute(
            new PmocDueInput(30, new DateOnly(2026, 12, 31), new DateOnly(2026, 9, 19), last));

        Assert.Equal(left.NextDueDate, right.NextDueDate);
        Assert.Equal(new DateOnly(2026, 10, 10), left.NextDueDate);
    }

    [Fact]
    public void Never_executed_asset_responds_to_first_due_date_edit()
    {
        var before = Compute(firstDue: new DateOnly(2026, 10, 1), asOf: new DateOnly(2026, 9, 19));
        var after = Compute(firstDue: new DateOnly(2026, 10, 20), asOf: new DateOnly(2026, 9, 19));

        Assert.Equal(new DateOnly(2026, 10, 1), before.NextDueDate);
        Assert.Equal(new DateOnly(2026, 10, 20), after.NextDueDate);
    }

    [Fact]
    public void Completed_without_completed_date_throws_without_scheduled_fallback()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PmocDueCalculator.PickLastCompleted(
            [
                (Guid.NewGuid(), WorkOrderStatus.Completed, new DateOnly(2026, 10, 20), null),
            ]));

        Assert.Contains("CompletedDate", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ScheduledDate", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public void Interval_days_outside_range_is_rejected(int intervalDays)
    {
        Assert.Throws<ArgumentException>(() =>
            PmocDueCalculator.EnsureIntervalDays(intervalDays));
    }

    [Theory]
    [InlineData(PmocScheduling.MinIntervalDays)]
    [InlineData(PmocScheduling.MaxIntervalDays)]
    public void Interval_days_bounds_are_accepted(int intervalDays)
    {
        PmocDueCalculator.EnsureIntervalDays(intervalDays);
        var result = Compute(intervalDays: intervalDays, firstDue: new DateOnly(2026, 10, 1), asOf: new DateOnly(2026, 9, 19));
        Assert.Equal(PmocDueStatus.NotDue, result.DueStatus);
    }

    [Fact]
    public void Status_completed_path_always_assigns_completed_date()
    {
        var source = File.ReadAllText(FindRepoFile(Path.Combine("Platform.Api", "Modules", "WorkOrders", "Services", "WorkOrderService.cs")));
        Assert.Contains("if (request.Status == WorkOrderStatus.Completed)", source, StringComparison.Ordinal);
        Assert.Contains("workOrder.CompletedDate = DateTimeOffset.UtcNow;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CompletedDate = workOrder.ScheduledDate", source, StringComparison.Ordinal);
    }

    private static PmocDueResult Compute(
        DateOnly firstDue,
        DateOnly asOf,
        int intervalDays = 30,
        PmocLastCompleted? last = null) =>
        PmocDueCalculator.Compute(new PmocDueInput(intervalDays, firstDue, asOf, last));

    private static PmocDueResult ComputeExecuted(DateOnly completedCivil, DateOnly asOf) =>
        Compute(
            firstDue: new DateOnly(2026, 1, 1),
            asOf: asOf,
            last: new PmocLastCompleted(BrazilNoon(completedCivil), completedCivil, Guid.NewGuid()));

    private static DateTimeOffset BrazilNoon(int year, int month, int day) =>
        BrazilNoon(new DateOnly(year, month, day));

    private static DateTimeOffset BrazilNoon(DateOnly civil) =>
        BrazilTimeZone.AtLocal(civil, new TimeOnly(12, 0));

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate vlr-api repository root.");
    }
}
