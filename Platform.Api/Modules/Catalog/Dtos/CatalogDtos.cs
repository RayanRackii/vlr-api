using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog.Dtos;

public sealed record CatalogProductListQuery(
    string? Name,
    string? Code,
    bool? IsActive);

public sealed record CreateCatalogProductRequest
{
    public required string Name { get; init; }

    public string? Code { get; init; }

    public string? Description { get; init; }

    public decimal? Price { get; init; }

    public string? Currency { get; init; }
}

public sealed record UpdateCatalogProductRequest
{
    public required string Name { get; init; }

    public string? Code { get; init; }

    public string? Description { get; init; }

    public decimal? Price { get; init; }

    public string? Currency { get; init; }
}

public sealed record CatalogProductFileDto(
    Guid Id,
    string FileName,
    string MimeType,
    long SizeBytes,
    CatalogFileVisibility Visibility,
    string? Url);

public sealed record CatalogProductResponse(
    Guid Id,
    string Name,
    string? Code,
    string? Description,
    decimal? Price,
    string Currency,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CatalogProductFileDto> Files);

public sealed record CatalogFileUrlResponse(string Url, bool IsPublic);

public sealed record CatalogOrderListQuery(
    int? OrderNumber,
    CatalogOrderStatus? Status,
    Guid? CustomerId,
    DateTimeOffset? From,
    DateTimeOffset? To);

public sealed record CatalogOrderItemResponse(
    Guid Id,
    Guid ProductId,
    string ProductName,
    string? ProductCode,
    decimal? UnitPrice,
    string Currency,
    int Quantity,
    decimal? SubTotal);

public sealed record CatalogOrderHistoryResponse(
    Guid Id,
    CatalogOrderStatus Status,
    CatalogActorType ActorType,
    Guid? ActorId,
    string? Reason,
    DateTimeOffset CreatedAt);

public sealed record CatalogOrderResponse(
    Guid Id,
    string DisplayNumber,
    int OrderNumber,
    CatalogOrderStatus Status,
    Guid CustomerId,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    string? CustomerNote,
    decimal? TotalAmount,
    string Currency,
    string? RejectedReason,
    string? CancelledReason,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CatalogOrderItemResponse> Items,
    IReadOnlyList<CatalogOrderHistoryResponse> History);

public sealed record CatalogReasonRequest
{
    public string? Reason { get; init; }
}

public sealed record ProductRequestFileDto(
    Guid Id,
    string FileName,
    string MimeType,
    long SizeBytes);

public sealed record ProductRequestResponse(
    Guid Id,
    Guid CustomerId,
    string Description,
    int Quantity,
    string? Note,
    ProductRequestStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ProductRequestFileDto> Files);

public sealed record PortalProductFileDto(
    Guid Id,
    string FileName,
    string MimeType,
    long SizeBytes,
    string Url);

public sealed record PortalProductResponse(
    Guid Id,
    string Name,
    string? Code,
    string? Description,
    decimal? Price,
    string Currency,
    IReadOnlyList<PortalProductFileDto> Files);

public sealed record CreatePortalOrderItemRequest
{
    public required Guid ProductId { get; init; }

    public required int Quantity { get; init; }
}

public sealed record CreatePortalOrderRequest
{
    public required IReadOnlyList<CreatePortalOrderItemRequest> Items { get; init; }

    public string? CustomerNote { get; init; }
}

public sealed record CreateProductRequestDto
{
    public required string Description { get; init; }

    public required int Quantity { get; init; }

    public string? Note { get; init; }
}

public sealed record CatalogNotificationListQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? EventType,
    NotificationRecipientKind? RecipientKind,
    NotificationChannel? Channel,
    NotificationDeliveryStatus? Status);

public sealed record CatalogNotificationDeliveryResponse(
    Guid Id,
    Guid NotificationId,
    string EventType,
    NotificationChannel Channel,
    NotificationRecipientKind RecipientKind,
    Guid? RecipientId,
    string? RecipientName,
    NotificationDeliveryStatus Status,
    int AttemptCount,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

public sealed record CatalogChannelConfigItem(
    string EventType,
    NotificationChannel Channel,
    bool IsActive);

public sealed record UpsertCatalogChannelConfigRequest
{
    public required string EventType { get; init; }

    public required NotificationChannel Channel { get; init; }

    public required bool IsActive { get; init; }
}
