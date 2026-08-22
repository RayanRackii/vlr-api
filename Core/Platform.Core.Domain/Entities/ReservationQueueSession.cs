using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Daily opening session for a Location waiting queue. Schema: rentals.
/// Keyed by (TenantId, RentalAssetId, OpeningDate) — the civil date of T in America/Sao_Paulo.
/// </summary>
public class ReservationQueueSession : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid RentalAssetId { get; set; }

    public required DateOnly OpeningDate { get; set; }

    public required DateTimeOffset OpensAt { get; set; }

    public required DateTimeOffset WaitingRoomOpensAt { get; set; }

    public RentalAsset RentalAsset { get; set; } = null!;

    private readonly List<ReservationQueueTicket> _tickets = [];

    public IReadOnlyCollection<ReservationQueueTicket> Tickets => _tickets.AsReadOnly();

    public void AddTicket(ReservationQueueTicket ticket)
    {
        _tickets.Add(ticket);
    }
}
