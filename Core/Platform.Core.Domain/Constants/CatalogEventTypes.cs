namespace Platform.Core.Domain.Constants;

public static class CatalogEventTypes
{
    public const string OrderCreated = "catalog.order.created";
    public const string OrderApproved = "catalog.order.approved";
    public const string OrderPreparing = "catalog.order.preparing";
    public const string OrderReady = "catalog.order.ready";
    public const string OrderRejected = "catalog.order.rejected";
    public const string OrderCancelledBySupplier = "catalog.order.cancelled_by_supplier";

    public static readonly string[] All =
    [
        OrderCreated,
        OrderApproved,
        OrderPreparing,
        OrderReady,
        OrderRejected,
        OrderCancelledBySupplier,
    ];

    public static readonly string[] Notifying =
    [
        OrderCreated,
        OrderApproved,
        OrderReady,
        OrderRejected,
        OrderCancelledBySupplier,
    ];
}
