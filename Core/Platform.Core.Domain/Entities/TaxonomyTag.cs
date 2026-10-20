using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class TaxonomyTag : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; init; }
    public required string NormalizedName { get; set; }
}
