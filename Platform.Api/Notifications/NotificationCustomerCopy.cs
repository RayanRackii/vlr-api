using Platform.Core.Domain.Enums;

namespace Platform.Api.Notifications;

public static class NotificationCustomerCopy
{
    public static string Label(CatalogOrderStatus status) => status switch
    {
        CatalogOrderStatus.Requested => "Solicitado",
        CatalogOrderStatus.Approved => "Aprovado",
        CatalogOrderStatus.Preparing => "Em preparação",
        CatalogOrderStatus.Ready => "Pronto",
        CatalogOrderStatus.Completed => "Concluído",
        CatalogOrderStatus.Rejected => "Recusado",
        CatalogOrderStatus.Cancelled => "Cancelado",
        _ => status.ToString(),
    };

    public static string Label(ReservationStatus status) => status switch
    {
        ReservationStatus.PendingDeposit => "Aguardando depósito",
        ReservationStatus.Confirmed => "Confirmada",
        ReservationStatus.Canceled => "Cancelada",
        ReservationStatus.Completed => "Concluída",
        _ => status.ToString(),
    };
}
