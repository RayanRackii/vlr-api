using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// A scheduled removal of one disabled module's data for a tenant (<c>core.tenant_module_data_purges</c>,
/// spec docs/plans/active/2026-11-20-module-data-purge.md). Pending while neither
/// <see cref="CancelledAt"/> nor <see cref="ExecutedAt"/> is set; at most one pending row per
/// tenant and module. Deliberately not <see cref="ITenantScoped"/>: it is platform admin data, always
/// read and written with an explicit <see cref="TenantId"/> predicate, so the ambient tenant filter
/// can never hide it from the admin endpoints or the daily job.
/// </summary>
public class TenantModuleDataPurge : Entity
{
    /// <summary>Grace period between scheduling and execution.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);

    public const int ModuleMaxLength = 64;

    public const int ActorMaxLength = 128;

    public const int CancelReasonMaxLength = 500;

    public Guid TenantId { get; set; }

    /// <summary>Canonical module key (<c>PlatformModules</c>).</summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>Auth subject (<c>sub</c>) of the platform admin who scheduled it.</summary>
    public string? RequestedByUserId { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    /// <summary><see cref="RequestedAt"/> plus <see cref="GracePeriod"/>.</summary>
    public DateTimeOffset ScheduledFor { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Auth subject of the admin who cancelled it; null when the job cancelled it.</summary>
    public string? CancelledByUserId { get; set; }

    /// <summary>Free text from an admin, or a system code (<c>module.reenabled</c>, <c>timeout</c>, ...).</summary>
    public string? CancelReason { get; set; }

    public DateTimeOffset? ExecutedAt { get; set; }

    /// <summary>jsonb object with counts only (rows deleted per table, storage objects). Never row contents.</summary>
    public string? ResultJson { get; set; }

    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public bool IsPending => CancelledAt is null && ExecutedAt is null;

    public void StampConcurrency()
    {
        RowVersion = Guid.NewGuid().ToByteArray();
        Touch();
    }

    public void Cancel(DateTimeOffset at, string? actor, string? reason)
    {
        CancelledAt = at;
        CancelledByUserId = actor;
        CancelReason = reason;
        StampConcurrency();
    }
}
