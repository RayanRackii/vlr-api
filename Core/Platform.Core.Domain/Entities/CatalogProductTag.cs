using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class CatalogProductTag : ITenantScoped
{
    public required Guid TenantId { get; set; }
    public required Guid ProductId { get; set; }
    public required Guid TagId { get; set; }
}
