using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>
/// Stable translation keys for order timeline entries. The web resolves <c>messageKey</c> with
/// <c>messageParams</c> in the user's language; <c>reason</c> stays as the raw recorded text.
/// </summary>
public static class CatalogOrderHistoryMessages
{
    private const string Prefix = "catalog.order.history.";

    public static string KeyFor(CatalogOrderStatus status, string? reason) =>
        reason switch
        {
            CatalogOrderUsageHistoryReasons.WorkFinished => Prefix + "usageWorkFinished",
            CatalogOrderUsageHistoryReasons.Released => Prefix + "usageReleased",
            _ => Prefix + char.ToLowerInvariant(status.ToString()[0]) + status.ToString()[1..],
        };

    public static IReadOnlyDictionary<string, string>? ParamsFor(string? reason) =>
        reason is not null
        && !string.IsNullOrWhiteSpace(reason)
        && reason is not (CatalogOrderUsageHistoryReasons.WorkFinished or CatalogOrderUsageHistoryReasons.Released)
            ? new Dictionary<string, string> { ["reason"] = reason }
            : null;

    internal static CatalogOrderHistoryResponse ToResponse(CatalogOrderStatusHistory h) =>
        new(h.Id, h.Status, h.ActorType, h.ActorId, h.Reason, h.CreatedAt)
        {
            MessageKey = KeyFor(h.Status, h.Reason),
            MessageParams = ParamsFor(h.Reason),
        };
}
