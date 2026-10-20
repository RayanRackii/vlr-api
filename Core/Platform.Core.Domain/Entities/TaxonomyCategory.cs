using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class TaxonomyCategory : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; init; }
    public Guid? ParentId { get; set; }
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
}
