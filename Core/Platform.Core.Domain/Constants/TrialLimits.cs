namespace Platform.Core.Domain.Constants;

/// <summary>
/// Trial lifecycle durations. Resource limits no longer live here: they come from the plan
/// catalog (<c>billing.plan_catalog.limits</c>) through the tenant's subscription.
/// New trials only; a trial created earlier keeps the dates stored on the tenant.
/// </summary>
public static class TrialLimits
{
    public const int TrialDays = 30;

    public const int PurgeDays = 90;
}
