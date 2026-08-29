using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class ProductRequest : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid CustomerId { get; set; }

    public required string Description { get; set; }

    public required int Quantity { get; set; }

    public string? Note { get; set; }

    public ProductRequestStatus Status { get; set; } = ProductRequestStatus.Submitted;

    private readonly List<ProductRequestFile> _files = [];

    public IReadOnlyCollection<ProductRequestFile> Files => _files.AsReadOnly();

    public Customer Customer { get; set; } = null!;

    public void AddFile(ProductRequestFile file) => _files.Add(file);
}
