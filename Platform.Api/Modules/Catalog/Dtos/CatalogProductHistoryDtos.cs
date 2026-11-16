namespace Platform.Api.Modules.Catalog.Dtos;

/// <summary>One changed field of a product history entry. Old/New are display-ready strings (null = empty).</summary>
public sealed record CatalogProductHistoryChange(string Field, string? Old, string? New);

/// <summary>
/// One product change, newest first in listings. <c>Action</c> is one of the
/// <c>CatalogProductHistoryActions</c> values (e.g. product.created, product.updated, product.activated).
/// <c>ActorName</c> is null for system changes.
/// </summary>
public sealed record CatalogProductHistoryEntryResponse(
    Guid Id,
    string Action,
    IReadOnlyList<CatalogProductHistoryChange> Changes,
    string? ActorName,
    DateTimeOffset CreatedAt);
