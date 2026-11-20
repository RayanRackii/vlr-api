using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// One <c>Idempotency-Key</c> claimed by a customer for a create operation. The row is inserted
/// before the resource is created (the unique index on tenant, scope, principal and key lets
/// exactly one concurrent request win) and completed with the created resource id afterwards.
/// A row without <see cref="ResourceId"/> is a claim still in flight (or one a crash abandoned).
/// Rows expire after <see cref="Retention"/>; the data retention job deletes the expired ones.
/// </summary>
public class IdempotencyKey : Entity, ITenantScoped
{
    public const int ScopeMaxLength = 100;
    public const int KeyMaxLength = 100;
    public const int RequestHashLength = 64;

    /// <summary>How long a key stays valid after it was first claimed.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public IdempotencyKey()
    {
    }

    public IdempotencyKey(
        Guid tenantId,
        string scope,
        Guid principalId,
        string key,
        string requestHash,
        DateTimeOffset now)
    {
        TenantId = tenantId;
        Scope = scope;
        PrincipalId = principalId;
        Key = key;
        RequestHash = requestHash;
        CreatedAt = now;
        ExpiresAt = now + Retention;
    }

    public Guid TenantId { get; set; }

    /// <summary>The operation the key belongs to, for example <c>catalog.portal.order.create</c>.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The customer that sent the key.</summary>
    public Guid PrincipalId { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the request payload the key was first used with.</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary>The created resource; null while the first request is still running.</summary>
    public Guid? ResourceId { get; set; }

    /// <summary>The HTTP status the first request answered with; null while it is still running.</summary>
    public int? ResponseStatus { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public void Complete(Guid resourceId, int responseStatus)
    {
        ResourceId = resourceId;
        ResponseStatus = responseStatus;
    }
}
