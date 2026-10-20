using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class CatalogProductCategory : ITenantScoped
{
    public required Guid TenantId { get; set; }
    public required Guid ProductId { get; set; }
    public required Guid CategoryId { get; set; }
}
