namespace Platform.Api.Services.Billing;

/// <summary>
/// A create was refused because the tenant plan limit is reached. Deliberately not an
/// <see cref="InvalidOperationException"/>: controllers map that type to a body with only an error
/// message, while this one reaches the platform exception handler, which answers 409 with
/// <c>{ error, code: "plan.limit_reached", limit, current, max, planKey }</c>.
/// </summary>
public sealed class PlanLimitReachedException(
    string limit,
    int current,
    int max,
    string planKey)
    : Exception($"The {planKey} plan allows at most {max} {limit.Replace('_', ' ')}.")
{
    public const string Code = "plan.limit_reached";

    /// <summary>One of the keys of <c>PlanLimitKeys</c>: assets, rentable_resources, users, units.</summary>
    public string Limit { get; } = limit;

    public int Current { get; } = current;

    public int Max { get; } = max;

    public string PlanKey { get; } = planKey;
}
