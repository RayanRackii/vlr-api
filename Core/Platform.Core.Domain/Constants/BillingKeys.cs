namespace Platform.Core.Domain.Constants;

/// <summary>Package keys of <c>billing.plan_catalog.package_key</c> (a package is a set of existing modules).</summary>
public static class PackageKeys
{
    public const string Reservas = "reservas";

    public const string Manutencao = "manutencao";

    public const string Locacao = "locacao";

    public static readonly IReadOnlyList<string> All = [Reservas, Manutencao, Locacao];

    public static bool IsKnown(string? key) =>
        key is not null && All.Contains(key, StringComparer.Ordinal);
}

/// <summary>Plan keys of <c>billing.plan_catalog.plan_key</c>.</summary>
public static class PlanKeys
{
    public const string Essencial = "essencial";

    public const string Profissional = "profissional";

    public const string Completo = "completo";

    public static readonly IReadOnlyList<string> All = [Essencial, Profissional, Completo];
}

/// <summary>Keys of the <c>limits</c> jsonb of a plan; a missing or null key means unlimited.</summary>
public static class PlanLimitKeys
{
    public const string Assets = "assets";

    public const string RentableResources = "rentable_resources";

    public const string Users = "users";

    public const string Units = "units";

    public const string WhatsappMessagesMonth = "whatsapp_messages_month";
}

public static class SubscriptionStatuses
{
    public const string Trialing = "trialing";

    public const string Active = "active";

    public const string PastDue = "past_due";

    public const string ReadOnly = "read_only";

    public const string Canceled = "canceled";

    public static readonly IReadOnlyList<string> All = [Trialing, Active, PastDue, ReadOnly, Canceled];

    public static bool IsKnown(string? status) =>
        status is not null && All.Contains(status, StringComparer.Ordinal);
}

public static class BillingCycles
{
    public const string Monthly = "monthly";

    public const string Annual = "annual";

    public static bool IsKnown(string? cycle) =>
        cycle is Monthly or Annual;
}

/// <summary>
/// <c>provider</c>: the payment provider drives the status (dunning may block). <c>manual</c>: the
/// owner bills outside the app; no automatic transition may block the tenant.
/// </summary>
public static class BillingModes
{
    public const string Provider = "provider";

    public const string Manual = "manual";

    public static bool IsKnown(string? mode) =>
        mode is Provider or Manual;
}

public static class SubscriptionEventTypes
{
    public const string Created = "created";

    public const string PlanChanged = "plan_changed";

    public const string PaymentReceived = "payment_received";

    public const string PaymentOverdue = "payment_overdue";

    public const string ReadOnly = "read_only";

    public const string Reactivated = "reactivated";

    public const string Canceled = "canceled";
}
