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

    // Only pre-migration customer-visible files of Public products use public storage.
    public bool IsLegacyPublic { get; set; }

    public CatalogFileStatus Status { get; set; } = CatalogFileStatus.Ready;

    public CatalogProduct Product { get; set; } = null!;
}
