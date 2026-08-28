using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class CatalogProductFile : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid ProductId { get; set; }

    public required string StorageKey { get; set; }

    public required string FileName { get; set; }

    public required string MimeType { get; set; }

    public required long SizeBytes { get; set; }

    public required CatalogFileVisibility Visibility { get; set; }

    public CatalogProduct Product { get; set; } = null!;
}
