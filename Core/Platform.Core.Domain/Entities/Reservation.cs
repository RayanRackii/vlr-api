using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// B2C reservation for rental assets. Schema: rentals.
/// </summary>
public class Reservation : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid UnitId { get; set; }

    public required Guid CustomerId { get; set; }

    /// <summary>Historical snapshot of the customer name at booking time.</summary>
    public required string CustomerName { get; set; }

    /// <summary>Historical snapshot of the customer WhatsApp/phone at booking time.</summary>
    public required string CustomerWhatsApp { get; set; }

    public required DateTimeOffset StartDateTime { get; set; }

    public required DateTimeOffset EndDateTime { get; set; }

    public required ReservationStatus Status { get; set; }

    public required decimal TotalAmount { get; set; }

    public required decimal DepositPaid { get; set; }

    public Unit Unit { get; set; } = null!;

    public Customer Customer { get; set; } = null!;

    private readonly List<ReservationItem> _items = [];

    public IReadOnlyCollection<ReservationItem> Items => _items.AsReadOnly();

    public void AddItem(ReservationItem item)
    {
        _items.Add(item);
    }
}
