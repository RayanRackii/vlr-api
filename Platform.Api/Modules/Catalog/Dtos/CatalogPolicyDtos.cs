using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog.Dtos;

public sealed record CatalogPolicyResponse(
    bool CustomerCanCancelOrders,
    CatalogOrderStatus OrderCancelableUpToStatus,
    bool CustomerCanCancelRequests,
    ProductRequestStatus RequestCancelableUpToStatus,
    string? CancellationPolicyText);

public sealed class UpsertCatalogPolicyRequest
{
    [JsonRequired]
    public bool CustomerCanCancelOrders { get; init; }
    [JsonRequired]
    public CatalogOrderStatus OrderCancelableUpToStatus { get; init; }
    [JsonRequired]
    public bool CustomerCanCancelRequests { get; init; }
    [JsonRequired]
    public ProductRequestStatus RequestCancelableUpToStatus { get; init; }
    [MaxLength(2000)]
    public string? CancellationPolicyText { get; init; }
}

public sealed record PortalCatalogPolicyResponse(
    bool CanCancelOrders,
    bool CanCancelRequests,
    CatalogCancellationUpToStatus UpToStatus,
    string? PolicyText);

public sealed record CatalogCancellationUpToStatus(
    CatalogOrderStatus Orders,
    ProductRequestStatus Requests);
