using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Platform catalog of asset families (spaces, electrical, goods, …).
/// FieldSchemaJson describes extra attribute fields rendered per family.
/// </summary>
public class AssetFamily : Entity
{
    public required string Key { get; set; }

    public required string Label { get; set; }

    /// <summary>JSON: { "fields": [ { "key", "type", "required", "label" } ] }</summary>
    public required string FieldSchemaJson { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}
