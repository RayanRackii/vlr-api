using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Visual arrangement of Rentables on a 2D canvas. Schema: rentals.
/// </summary>
public class RentalLayout : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public Guid? UnitId { get; set; }

    public required string Name { get; set; }

    public required bool IsActive { get; set; }

    public Unit? Unit { get; set; }

    private readonly List<RentalLayoutItem> _items = [];

    public IReadOnlyCollection<RentalLayoutItem> Items => _items.AsReadOnly();

    public void AddItem(RentalLayoutItem item) => _items.Add(item);
}
