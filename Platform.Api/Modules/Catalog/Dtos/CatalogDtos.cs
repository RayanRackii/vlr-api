using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog.Dtos;

// --- Order-resource planning (Phase 0) ---

/// <summary>One row of a product's resource-requirement configuration (read or write).</summary>
public sealed record CatalogProductResourceRequirementDto
{
    public Guid? Id { get; init; }

    public Guid? RentalAssetId { get; init; }

    public Guid? AssetCategoryId { get; init; }

    public required int ResourceQuantity { get; init; }

    public required int DurationMinutesPerUnit { get; init; }

    public int PrepMinutes { get; init; }

    public int CleanupMinutes { get; init; }
}

public sealed record CatalogProductResourceRequirementsResponse(
    int ExpectedVersion,
    IReadOnlyList<CatalogProductResourceRequirementDto> Requirements);

public sealed record ReplaceCatalogProductResourceRequirementsRequest
{
    public required int ExpectedVersion { get; init; }

    public required IReadOnlyList<CatalogProductResourceRequirementDto> Requirements { get; init; }
}

/// <summary>Per-order-item production state, derived from its allocation (if any).</summary>
public enum ProductionSummaryStatus
{
    NotRequired = 0,
    Unscheduled = 1,
    Scheduled = 2,
    InProgress = 3,
    Done = 4,
    Blocked = 5,
    Cancelled = 6,
}

public sealed record AllocationPreviewWorkStart
{
    public required Guid OrderItemId { get; init; }

    public required DateTimeOffset WorkStart { get; init; }
}

public sealed record CatalogOrderAllocationPreviewRequest
{
    public required Guid UnitId { get; init; }

    public required IReadOnlyList<AllocationPreviewWorkStart> WorkStarts { get; init; }
}

public sealed record CatalogOrderAllocationPreviewResponse(
    bool IsFeasible,
    Guid? OrderItemId,
    Guid? RentalAssetId,
    DateTimeOffset? WorkStart,
    DateTimeOffset? WorkEnd,
    DateTimeOffset? OccupiedStart,
    DateTimeOffset? OccupiedEnd,
    DateTimeOffset? EstimatedReadyAt,
    string? ConflictCode,
    string? Reason);

public sealed record CatalogOrderApprovePlanningRequest
{
    public Guid? UnitId { get; init; }

    public DateTimeOffset? WorkStart { get; init; }

    public int? ExpectedVersion { get; init; }
}

public sealed record CatalogOrderAllocationUsageRequest
{
    public required string Action { get; init; }

    public required int ExpectedVersion { get; init; }
}

public sealed record CatalogOrderItemAllocationResponse(
    Guid Id,
    Guid RequirementId,
    Guid RentalAssetId,
    AllocationStatus Status,
    DateTimeOffset PlannedWorkStart,
    DateTimeOffset PlannedWorkEnd,
    string TimeZoneId,
    DateTimeOffset? ActualStartAt,
    DateTimeOffset? ActualWorkFinishedAt,
    DateTimeOffset? ActualReleaseAt,
    int PlanningRevision);

public sealed record CatalogProductListQuery(
    string? Name,
    string? Code,
    bool? IsActive,
    TaxonomyFilter? Taxonomy = null);

public sealed record CreateCatalogProductRequest
{
    public required string Name { get; init; }

    public string? Code { get; init; }

    public string? Description { get; init; }

    public decimal? Price { get; init; }

    public string? Currency { get; init; }
    public TaxonomyAssignmentRequest? Taxonomy { get; init; }

}

public sealed record UpdateCatalogProductRequest
{
    public required string Name { get; init; }

    public string? Code { get; init; }

    public string? Description { get; init; }

    public decimal? Price { get; init; }

    public string? Currency { get; init; }
    public TaxonomyAssignmentRequest? Taxonomy { get; init; }

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
    IReadOnlyList<CatalogProductFileDto> Files)
{
    public TaxonomyResponse Taxonomy { get; init; } = TaxonomyResponse.Empty;

    public CatalogProductVisibility Visibility { get; init; } = CatalogProductVisibility.Public;

    public int GrantedCustomerCount { get; init; }

    public int GrantedGroupCount { get; init; }
}

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
    decimal? SubTotal,
    ProductionSummaryStatus ProductionSummary,
    CatalogOrderItemAllocationResponse? Allocation);

public sealed record CatalogOrderHistoryResponse(
    Guid Id,
    CatalogOrderStatus Status,
    CatalogActorType ActorType,
    Guid? ActorId,
    string? Reason,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Display name of the acting party — the staff user's full name for
    /// <see cref="CatalogActorType.B2BUser"/>, the customer's name for
    /// <see cref="CatalogActorType.Customer"/>, null for <see cref="CatalogActorType.System"/>.
    /// Resolved tenant-scoped during enrichment (staff reads only).
    /// </summary>
    public string? ActorName { get; init; }
}

// --- Agenda (read-only schedule) ---

public sealed record CatalogAgendaQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    Guid? ProductId,
    Guid? RentalAssetId)
{
    // Npgsql only accepts UTC DateTimeOffset parameters; normalize offsets like -03:00.
    private readonly DateTimeOffset _from = From.ToUniversalTime();
    private readonly DateTimeOffset _to = To.ToUniversalTime();

    public DateTimeOffset From { get => _from; init => _from = value.ToUniversalTime(); }
    public DateTimeOffset To { get => _to; init => _to = value.ToUniversalTime(); }
}

/// <summary>
/// One agenda row: an allocated resource occurrence, or a still-unscheduled requested/approved
/// line. <see cref="WorkStart"/>/<see cref="WorkEnd"/> are null for unscheduled lines;
/// <see cref="OccupiedStart"/>/<see cref="OccupiedEnd"/> include the prep/cleanup buffers.
/// </summary>
public sealed record CatalogAgendaItemResponse(
    Guid OrderId,
    string? DisplayNumber,
    CatalogOrderStatus OrderStatus,
    Guid ProductId,
    string ProductName,
    Guid? RentalAssetId,
    string? ResourceName,
    string? CustomerName,
    DateTimeOffset? WorkStart,
    DateTimeOffset? WorkEnd,
    DateTimeOffset OccupiedStart,
    DateTimeOffset OccupiedEnd,
    AllocationStatus? AllocationStatus,
    string TimeZoneId);

public sealed record CatalogAgendaResourceResponse(
    Guid Id,
    string Name,
    string? Category);

public sealed record CatalogAgendaResourcesResponse(
    IReadOnlyList<CatalogAgendaItemResponse> Items,
    IReadOnlyList<CatalogAgendaResourceResponse> Resources);

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
    IReadOnlyList<CatalogOrderHistoryResponse> History,
    string OrderVersion,
    int PlanningRevision,
    ProductionSummaryStatus ProductionSummary,
    DateTimeOffset? EstimatedReadyAt);

/// <summary>Portal-facing order shape: no equipment/capacity/allocation identifiers, ever.</summary>
public sealed record PortalCatalogOrderResponse(
    Guid Id,
    string DisplayNumber,
    int OrderNumber,
    CatalogOrderStatus Status,
    string? CustomerNote,
    decimal? TotalAmount,
    string Currency,
    string? RejectedReason,
    string? CancelledReason,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CatalogOrderItemResponse> Items,
    IReadOnlyList<CatalogOrderHistoryResponse> History,
    ProductionSummaryStatus ProductionSummary,
    DateTimeOffset? EstimatedReadyAt);

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

public sealed record PortalProductRequestFileDto(
    Guid Id,
    string FileName,
    string Url);

public sealed record PortalProductRequestResponse(
    Guid Id,
    ProductRequestStatus Status,
    string Description,
    int Quantity,
    string? Note,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PortalProductRequestFileDto> Files);

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
    IReadOnlyList<PortalProductFileDto> Files)
{
    public TaxonomyResponse Taxonomy { get; init; } = TaxonomyResponse.Empty;
}

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

public sealed record CustomerGroupResponse(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<Guid> MemberCustomerIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record UpsertCustomerGroupRequest(string Name, string? Description);

public sealed record CustomerGroupMembersRequest(IReadOnlyList<Guid> CustomerIds);

public sealed record CatalogProductAccessResponse(
    Guid ProductId,
    string Visibility,
    IReadOnlyList<Guid> CustomerIds,
    IReadOnlyList<Guid> GroupIds)
{
    /// <summary>True when the product is Restricted but nobody is granted (no customer can see it).</summary>
    public bool RestrictedWithoutGrants { get; init; }
}

public sealed record CatalogAccessAuditResponse(
    Guid Id,
    string Action,
    string EntityType,
    Guid EntityId,
    string ActorType,
    Guid? ActorUserId,
    string? ActorName,
    string? BeforeJson,
    string? AfterJson,
    DateTimeOffset CreatedAt);

public sealed record UpdateCatalogProductAccessRequest(
    string Visibility,
    IReadOnlyList<Guid>? CustomerIds,
    IReadOnlyList<Guid>? GroupIds);
