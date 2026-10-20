using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>One optional policy per tenant. Missing row preserves the original customer behavior.</summary>
public class CatalogPolicy : Entity, ITenantScoped
{
    public const int CancellationPolicyMaxLength = 2000;
    public required Guid TenantId { get; set; }
    public bool CustomerCanCancelOrders { get; set; } = true;
    public CatalogOrderStatus OrderCancelableUpToStatus { get; set; } = CatalogOrderStatus.Requested;
    public bool CustomerCanCancelRequests { get; set; }
    public ProductRequestStatus RequestCancelableUpToStatus { get; set; } = ProductRequestStatus.Submitted;
    public string? CancellationPolicyText { get; set; }
}
