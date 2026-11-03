using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class CatalogProduct : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Code { get; set; }

    public string? Description { get; set; }

    public decimal? Price { get; set; }

    public string Currency { get; set; } = "BRL";

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Portal visibility. <see cref="CatalogProductVisibility.Public"/> (default) is visible to
    /// everyone; <see cref="CatalogProductVisibility.Restricted"/> only to granted customers/groups.
    /// </summary>
    public CatalogProductVisibility Visibility { get; set; } = CatalogProductVisibility.Public;

    /// <summary>
    /// Concurrency token for the product's resource-requirements configuration.
    /// Orders capture this value at creation time into <see cref="CatalogOrder.ResourceRequirementsVersion"/>.
    /// </summary>
    public int ResourceRequirementsVersion { get; set; }

    private readonly List<CatalogProductFile> _files = [];

    public IReadOnlyCollection<CatalogProductFile> Files => _files.AsReadOnly();

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    public void Activate()
    {
        IsActive = true;
        Touch();
    }

    public void AddFile(CatalogProductFile file)
    {
        _files.Add(file);
        Touch();
    }

    public void RemoveFile(CatalogProductFile file)
    {
        _files.Remove(file);
        Touch();
    }
}
