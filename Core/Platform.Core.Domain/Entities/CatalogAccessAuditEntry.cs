using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Append-only history of customer-group and product-visibility changes made by staff
/// (shown in the UI as "Equipe"). Before/after are small JSON snapshots.
/// </summary>
public class CatalogAccessAuditEntry : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    /// <summary>e.g. group.created, group.updated, group.deleted, group.membersAdded, group.memberRemoved, product.accessChanged</summary>
    public required string Action { get; set; }
    /// <summary>"group" or "product".</summary>
    public required string EntityType { get; set; }
    public required Guid EntityId { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
}
