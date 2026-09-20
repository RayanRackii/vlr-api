using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Jobs;

public sealed record PmocLastCompleted(
    DateTimeOffset CompletedDate,
    DateOnly ScheduledDate,
    Guid Id);

public sealed record PmocDueInput(
    int IntervalDays,
    DateOnly FirstDueDate,
    DateOnly AsOfDate,
    PmocLastCompleted? LastCompleted);

public sealed record PmocDueResult(
    DateOnly NextDueDate,
    PmocHistoryStatus HistoryStatus,
    PmocDueStatus DueStatus,
    bool NeedsAttention);

public static class PmocDueCalculator
{
    public static PmocDueResult Compute(PmocDueInput input)
    {
        EnsureIntervalDays(input.IntervalDays);

        DateOnly nextDue;
        PmocHistoryStatus history;
        if (input.LastCompleted is { } last)
        {
            history = PmocHistoryStatus.Executed;
            nextDue = BrazilTimeZone.GetCivilDate(last.CompletedDate).AddDays(input.IntervalDays);
        }
        else
        {
            history = PmocHistoryStatus.NeverExecuted;
            nextDue = input.FirstDueDate;
        }

        var due = nextDue == input.AsOfDate
            ? PmocDueStatus.DueToday
            : nextDue < input.AsOfDate
                ? PmocDueStatus.Overdue
                : PmocDueStatus.NotDue;

        return new PmocDueResult(
            nextDue,
            history,
            due,
            due is PmocDueStatus.DueToday or PmocDueStatus.Overdue);
    }

    public static PmocLastCompleted? PickLastCompleted(
        IEnumerable<(Guid Id, WorkOrderStatus Status, DateOnly ScheduledDate, DateTimeOffset? CompletedDate)> relevant)
    {
        var last = relevant
            .Where(item => item.Status == WorkOrderStatus.Completed)
            .OrderByDescending(item => item.CompletedDate)
            .ThenByDescending(item => item.ScheduledDate)
            .ThenByDescending(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.ScheduledDate,
                item.CompletedDate,
            })
            .FirstOrDefault();

        if (last is null)
        {
            return null;
        }

        if (last.CompletedDate is null)
        {
            throw new InvalidOperationException(
                $"Completed WorkOrder '{last.Id}' has no CompletedDate.");
        }

        return new PmocLastCompleted(last.CompletedDate.Value, last.ScheduledDate, last.Id);
    }

    public static void EnsureIntervalDays(int intervalDays)
    {
        if (intervalDays is < PmocScheduling.MinIntervalDays or > PmocScheduling.MaxIntervalDays)
        {
            throw new ArgumentException(
                $"IntervalDays must be between {PmocScheduling.MinIntervalDays} and {PmocScheduling.MaxIntervalDays}.");
        }
    }
}
