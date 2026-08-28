using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class ProductRequestFile : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid ProductRequestId { get; set; }

    public required string StorageKey { get; set; }

    public required string FileName { get; set; }

    public required string MimeType { get; set; }

    public required long SizeBytes { get; set; }

    public CatalogFileVisibility Visibility { get; set; } = CatalogFileVisibility.InternalB2B;

    public ProductRequest ProductRequest { get; set; } = null!;
}
