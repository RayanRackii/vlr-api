using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;

namespace Platform.Core.Domain.Entities;

public class CatalogOrder : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid CustomerId { get; set; }

    public int OrderNumber { get; set; }

    public CatalogOrderStatus Status { get; private set; } = CatalogOrderStatus.Requested;

    public string? CustomerNote { get; set; }

    public required string CustomerNameSnapshot { get; set; }

    public string? CustomerEmailSnapshot { get; set; }

    public string? CustomerPhoneSnapshot { get; set; }

    public decimal? TotalAmount { get; set; }

    public string Currency { get; set; } = "BRL";

    public string? RejectedReason { get; private set; }

    public string? CancelledReason { get; private set; }

    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    private readonly List<CatalogOrderItem> _items = [];

    private readonly List<CatalogOrderStatusHistory> _history = [];

    public IReadOnlyCollection<CatalogOrderItem> Items => _items.AsReadOnly();

    public IReadOnlyCollection<CatalogOrderStatusHistory> History => _history.AsReadOnly();

    public Customer Customer { get; set; } = null!;

    public void AddItem(CatalogOrderItem item)
    {
        if (Status != CatalogOrderStatus.Requested)
        {
            throw new InvalidCatalogOrderTransitionException(
                "Order items are immutable after approval.");
        }

        _items.Add(item);
    }

    public void RecordHistory(
        CatalogOrderStatus status,
        CatalogActorType actorType,
        Guid? actorId,
        string? reason)
    {
        _history.Add(new CatalogOrderStatusHistory
        {
            TenantId = TenantId,
            OrderId = Id,
            Status = status,
            ActorType = actorType,
            ActorId = actorId,
            Reason = reason,
        });
    }

    public void Approve(CatalogActorType actorType, Guid? actorId)
    {
        EnsureStatus(CatalogOrderStatus.Requested);
        Status = CatalogOrderStatus.Approved;
        RecordHistory(Status, actorType, actorId, reason: null);
        StampConcurrency();
    }

    public void StartPreparing(CatalogActorType actorType, Guid? actorId)
    {
        EnsureStatus(CatalogOrderStatus.Approved);
        Status = CatalogOrderStatus.Preparing;
        RecordHistory(Status, actorType, actorId, reason: null);
        StampConcurrency();
    }

    public void MarkReady(CatalogActorType actorType, Guid? actorId)
    {
        EnsureStatus(CatalogOrderStatus.Preparing);
        Status = CatalogOrderStatus.Ready;
        RecordHistory(Status, actorType, actorId, reason: null);
        StampConcurrency();
    }

    public void Complete(CatalogActorType actorType, Guid? actorId)
    {
        EnsureStatus(CatalogOrderStatus.Ready);
        Status = CatalogOrderStatus.Completed;
        RecordHistory(Status, actorType, actorId, reason: null);
        StampConcurrency();
    }

    public void Reject(string reason, CatalogActorType actorType, Guid? actorId)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason is required.");
        }

        EnsureStatus(CatalogOrderStatus.Requested);
        Status = CatalogOrderStatus.Rejected;
        RejectedReason = reason.Trim();
        RecordHistory(Status, actorType, actorId, RejectedReason);
        StampConcurrency();
    }

    public void Cancel(CatalogActorType actor, string? reason, Guid? actorId)
    {
        if (actor == CatalogActorType.Customer)
        {
            EnsureStatus(CatalogOrderStatus.Requested);
            Status = CatalogOrderStatus.Cancelled;
            RecordHistory(Status, actor, actorId, reason: null);
            StampConcurrency();
            return;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reason is required.");
        }

        EnsureStatus(
            CatalogOrderStatus.Requested,
            CatalogOrderStatus.Approved,
            CatalogOrderStatus.Preparing,
            CatalogOrderStatus.Ready);

        Status = CatalogOrderStatus.Cancelled;
        CancelledReason = reason.Trim();
        RecordHistory(Status, actor, actorId, CancelledReason);
        StampConcurrency();
    }

    private void StampConcurrency()
    {
        RowVersion = Guid.NewGuid().ToByteArray();
        Touch();
    }

    private void EnsureStatus(params CatalogOrderStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new InvalidCatalogOrderTransitionException(
                $"Cannot transition order from {Status}.");
        }
    }
}
