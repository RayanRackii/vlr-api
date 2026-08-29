using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Per-tenant monotonic order number. Primary key is <see cref="TenantId"/>.
/// </summary>
public class CatalogOrderNumberSequence : ITenantScoped
{
    public Guid TenantId { get; set; }

    public int LastNumber { get; set; }
}
