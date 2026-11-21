using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Services.Billing;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Jobs;

/// <summary>
/// Daily (America/Sao_Paulo): 1) a provider-billed subscription that has been <c>past_due</c> for 7 days becomes
/// <c>read_only</c> (event <c>read_only</c>); 2) when <c>Billing:Dunning:EmailsEnabled</c> is on, the reminder
/// e-mails of the billing calendar go to the tenant billing admins and the company contact: next charge D-3 and D0,
/// overdue D+1 and D+5, read-only notice at D+7, and the trial end at D-5, D-1 and D0.
/// The state transition always runs; only the e-mails are behind the flag (the provider also e-mails customers).
/// Only <c>billing_mode = provider</c> rows with a <c>provider_subscription_id</c> are ever touched: manual and
/// backfilled rows never become read-only. Each notice is recorded once per (subscription, kind, period) in
/// <c>subscription_events</c> (type <c>dunning_&lt;kind&gt;</c>, unique <c>provider_event_id</c> key), so reruns and
/// overlapping runs never send twice. Billing rows are not tenant scoped, so the job selects by explicit predicates
/// in a platform scope.
/// </summary>
public sealed class BillingDunningJob(
    AppDbContext dbContext,
    IBillingDunningMailer mailer,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<BillingDunningJob> logger)
{
    public const string EmailsEnabledKey = "Billing:Dunning:EmailsEnabled";

    /// <summary>Rows per query: the single Hangfire worker must stay short.</summary>
    public const int BatchSize = 200;

    private sealed record Candidate(Guid SubscriptionId, Guid TenantId, DunningKind Kind, DateOnly Period, DateOnly? Date);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "billing-dunning");
        var now = timeProvider.GetUtcNow();
        var today = BrazilTimeZone.GetCivilDate(now);

        var movedToReadOnly = await MoveOverdueToReadOnlyAsync(now, today, cancellationToken);

        var emailed = 0;
        var enabled = configuration.GetValue<bool>(EmailsEnabledKey);
        if (enabled)
        {
            var candidates = new List<Candidate>();
            candidates.AddRange(await ProviderCandidatesAsync(today, cancellationToken));
            candidates.AddRange(await TrialCandidatesAsync(today, cancellationToken));
            emailed = await SendAsync(candidates, cancellationToken);
        }

        logger.LogInformation(
            "BillingDunningJob finished. Read-only: {ReadOnly}. E-mails enabled: {Enabled}. Notices sent: {Sent}.",
            movedToReadOnly,
            enabled,
            emailed);
    }

    private async Task<int> MoveOverdueToReadOnlyAsync(DateTimeOffset now, DateOnly today, CancellationToken cancellationToken)
    {
        // Superset by instant (the civil boundary is checked per row): overdue since before the start of day today-6.
        var latestPastDue = BrazilTimeZone.StartOfCivilDay(today.AddDays(1 - BillingDunningSchedule.ReadOnlyAfterDays));

        var due = await dbContext.TenantSubscriptions
            .AsNoTracking()
            .Where(s => s.EndedAt == null
                && s.BillingMode == BillingModes.Provider
                && s.ProviderSubscriptionId != null
                && s.Status == SubscriptionStatuses.PastDue
                && s.PastDueSince != null
                && s.PastDueSince < latestPastDue)
            .OrderBy(s => s.PastDueSince)
            .Select(s => s.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var moved = 0;
        foreach (var subscriptionId in due)
        {
            try
            {
                var subscription = await dbContext.TenantSubscriptions
                    .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);
                if (subscription is null
                    || subscription.EndedAt is not null
                    || subscription.BillingMode != BillingModes.Provider
                    || string.IsNullOrEmpty(subscription.ProviderSubscriptionId)
                    || subscription.Status != SubscriptionStatuses.PastDue
                    || subscription.PastDueSince is not { } pastDueSince
                    || !BillingDunningSchedule.IsReadOnlyDue(pastDueSince, today))
                {
                    continue;
                }

                subscription.Status = SubscriptionStatuses.ReadOnly;
                subscription.StampConcurrency();
                dbContext.SubscriptionEvents.Add(new SubscriptionEvent
                {
                    TenantId = subscription.TenantId,
                    SubscriptionId = subscription.Id,
                    Type = SubscriptionEventTypes.ReadOnly,
                    PayloadJson = JsonSerializer.Serialize(new { source = "dunning", pastDueSince }),
                });
                await dbContext.SaveChangesAsync(cancellationToken);
                moved++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad row (or a payment racing in) must not stop the others; it is retried on the next run.
                dbContext.ChangeTracker.Clear();
                logger.LogWarning(ex, "Could not make subscription {SubscriptionId} read-only.", subscriptionId);
            }
        }

        return moved;
    }

    private async Task<List<Candidate>> ProviderCandidatesAsync(DateOnly today, CancellationToken cancellationToken)
    {
        // Next charge today..today+3 (D0, D-3); overdue since today-5..today-1 (D+1, D+5); read-only since today-7.
        var nextDueFrom = BrazilTimeZone.StartOfCivilDay(today);
        var nextDueTo = BrazilTimeZone.StartOfCivilDay(today.AddDays(4));
        var overdueFrom = BrazilTimeZone.StartOfCivilDay(today.AddDays(-5));
        var overdueTo = BrazilTimeZone.StartOfCivilDay(today);
        var readOnlyFrom = BrazilTimeZone.StartOfCivilDay(today.AddDays(-BillingDunningSchedule.ReadOnlyAfterDays));
        var readOnlyTo = BrazilTimeZone.StartOfCivilDay(today.AddDays(1 - BillingDunningSchedule.ReadOnlyAfterDays));

        var rows = await dbContext.TenantSubscriptions
            .AsNoTracking()
            .Where(s => s.EndedAt == null
                && s.BillingMode == BillingModes.Provider
                && s.ProviderSubscriptionId != null
                && ((s.Status == SubscriptionStatuses.Active
                        && !s.CancelAtPeriodEnd
                        && s.CurrentPeriodEnd != null
                        && s.CurrentPeriodEnd >= nextDueFrom
                        && s.CurrentPeriodEnd < nextDueTo)
                    || (s.Status == SubscriptionStatuses.PastDue
                        && s.PastDueSince != null
                        && s.PastDueSince >= overdueFrom
                        && s.PastDueSince < overdueTo)
                    || (s.Status == SubscriptionStatuses.ReadOnly
                        && s.PastDueSince != null
                        && s.PastDueSince >= readOnlyFrom
                        && s.PastDueSince < readOnlyTo)))
            .OrderBy(s => s.CreatedAt)
            .Select(s => new { s.Id, s.TenantId, s.Status, s.CurrentPeriodEnd, s.PastDueSince })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var result = new List<Candidate>();
        foreach (var row in rows)
        {
            DunningKind? kind;
            DateTimeOffset reference;
            switch (row.Status)
            {
                case SubscriptionStatuses.Active when row.CurrentPeriodEnd is { } nextDue:
                    reference = nextDue;
                    kind = BillingDunningSchedule.ForActive(nextDue, today);
                    break;
                case SubscriptionStatuses.PastDue when row.PastDueSince is { } since:
                    reference = since;
                    kind = BillingDunningSchedule.ForPastDue(since, today);
                    break;
                case SubscriptionStatuses.ReadOnly when row.PastDueSince is { } since:
                    reference = since;
                    kind = BillingDunningSchedule.ForReadOnly(since, today);
                    break;
                default:
                    continue;
            }

            if (kind is null)
            {
                continue;
            }

            var period = BillingDunningSchedule.BrazilCivil(reference);
            result.Add(new Candidate(row.Id, row.TenantId, kind, period, period));
        }

        return result;
    }

    private async Task<List<Candidate>> TrialCandidatesAsync(DateOnly today, CancellationToken cancellationToken)
    {
        // Trial ends today..today+5 (D0, D-1, D-5). A tenant that already started a checkout is not nagged.
        var from = BrazilTimeZone.StartOfCivilDay(today);
        var to = BrazilTimeZone.StartOfCivilDay(today.AddDays(6));

        var rows = await (
                from subscription in dbContext.TenantSubscriptions.AsNoTracking()
                join tenant in dbContext.Tenants.AsNoTracking() on subscription.TenantId equals tenant.Id
                where subscription.EndedAt == null
                    && subscription.Status == SubscriptionStatuses.Trialing
                    && subscription.ProviderSubscriptionId == null
                    && tenant.IsTrial
                    && tenant.IsActive
                    && tenant.TrialEndsAt != null
                    && tenant.TrialEndsAt >= from
                    && tenant.TrialEndsAt < to
                orderby tenant.TrialEndsAt
                select new { subscription.Id, subscription.TenantId, tenant.TrialEndsAt })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var result = new List<Candidate>();
        foreach (var row in rows)
        {
            if (row.TrialEndsAt is not { } endsAt
                || BillingDunningSchedule.ForTrial(endsAt, today) is not { } kind)
            {
                continue;
            }

            var period = BillingDunningSchedule.BrazilCivil(endsAt);
            result.Add(new Candidate(row.Id, row.TenantId, kind, period, period));
        }

        return result;
    }

    private async Task<int> SendAsync(List<Candidate> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return 0;
        }

        var tenantIds = candidates.Select(c => c.TenantId).Distinct().ToList();
        var tenants = (await dbContext.Tenants
                .AsNoTracking()
                .Where(t => tenantIds.Contains(t.Id) && t.IsActive)
                .Select(t => new { t.Id, t.LegalName, t.TradeName, t.ContactEmail })
                .ToListAsync(cancellationToken))
            .ToDictionary(t => t.Id);

        var sent = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                if (!tenants.TryGetValue(candidate.TenantId, out var tenant))
                {
                    continue;
                }

                var key = BillingDunningSchedule.DedupeKey(candidate.SubscriptionId, candidate.Kind, candidate.Period);
                if (await dbContext.SubscriptionEvents.AnyAsync(e => e.ProviderEventId == key, cancellationToken))
                {
                    continue;
                }

                var recipients = await mailer.ResolveRecipientsAsync(candidate.TenantId, tenant.ContactEmail, cancellationToken);
                if (recipients.Count == 0)
                {
                    logger.LogInformation(
                        "No billing recipient for tenant {TenantId}; {Kind} notice skipped.",
                        candidate.TenantId,
                        candidate.Kind.Key);
                    continue;
                }

                // Record first, send second: a concurrent run loses on the unique key and sends nothing.
                dbContext.SubscriptionEvents.Add(new SubscriptionEvent
                {
                    TenantId = candidate.TenantId,
                    SubscriptionId = candidate.SubscriptionId,
                    Type = candidate.Kind.EventType,
                    ProviderEventId = key,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        source = "dunning",
                        kind = candidate.Kind.Key,
                        period = candidate.Period.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        recipients = recipients.Count,
                    }),
                });
                await dbContext.SaveChangesAsync(cancellationToken);

                var model = new BillingEmailModel(
                    string.IsNullOrWhiteSpace(tenant.TradeName) ? tenant.LegalName : tenant.TradeName!,
                    candidate.Date,
                    mailer.PlanUrl);
                await mailer.SendAsync(candidate.Kind, recipients, model, cancellationToken);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                dbContext.ChangeTracker.Clear();
                logger.LogWarning(
                    ex,
                    "Could not send the {Kind} notice of subscription {SubscriptionId}.",
                    candidate.Kind.Key,
                    candidate.SubscriptionId);
            }
        }

        return sent;
    }
}
