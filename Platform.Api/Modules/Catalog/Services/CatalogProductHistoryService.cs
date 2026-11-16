using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>Action keys written to <c>catalog.catalog_access_audit</c> (EntityType "product").</summary>
public static class CatalogProductHistoryActions
{
    public const string EntityType = "product";
    public const string Created = "product.created";
    public const string Updated = "product.updated";
    public const string Activated = "product.activated";
    public const string Deactivated = "product.deactivated";
    public const string TaxonomyChanged = "product.taxonomyChanged";
    public const string RequirementsChanged = "product.requirementsChanged";
    public const string FileAdded = "product.fileAdded";
    public const string FileRemoved = "product.fileRemoved";
    /// <summary>Pre-existing action written by <c>CustomerGroupService</c>; its rows are kept as is.</summary>
    public const string AccessChanged = "product.accessChanged";
    public const string Duplicated = "product.duplicated";
    public const string Deleted = "product.deleted";
}

/// <summary>
/// Stages a product history entry in the current <see cref="AppDbContext"/> WITHOUT saving, so it
/// commits in the same SaveChanges (and transaction) as the change it describes. The caller owns
/// SaveChanges. Example: <c>await recorder.RecordAsync(id, CatalogProductHistoryActions.Duplicated, changes, ct)</c>.
/// </summary>
public interface IProductHistoryRecorder
{
    Task RecordAsync(
        Guid productId,
        string action,
        IReadOnlyList<CatalogProductHistoryChange> changes,
        CancellationToken cancellationToken);
}

public interface ICatalogProductHistoryService : IProductHistoryRecorder
{
    /// <summary>Newest first; null when the product does not exist in the current tenant.</summary>
    Task<IReadOnlyList<CatalogProductHistoryEntryResponse>?> ListAsync(
        Guid productId,
        int limit,
        DateTimeOffset? before,
        CancellationToken cancellationToken);
}

public sealed class CatalogProductHistoryService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IHttpContextAccessor? httpContextAccessor = null) : ICatalogProductHistoryService
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private (Guid? Id, string? Name)? _actor;

    public async Task RecordAsync(
        Guid productId,
        string action,
        IReadOnlyList<CatalogProductHistoryChange> changes,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var (actorId, actorName) = await ResolveActorAsync(cancellationToken);
        dbContext.CatalogAccessAudit.Add(new CatalogAccessAuditEntry
        {
            TenantId = tenantId,
            ActorUserId = actorId,
            ActorName = actorName,
            Action = action,
            EntityType = CatalogProductHistoryActions.EntityType,
            EntityId = productId,
            AfterJson = JsonSerializer.Serialize(new StoredChanges(changes), Json),
        });
    }

    public async Task<IReadOnlyList<CatalogProductHistoryEntryResponse>?> ListAsync(
        Guid productId,
        int limit,
        DateTimeOffset? before,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var exists = await dbContext.CatalogProducts.AsNoTracking()
            .AnyAsync(p => p.TenantId == tenantId && p.Id == productId, cancellationToken);
        if (!exists) return null;

        var take = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
        var query = dbContext.CatalogAccessAudit.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                && a.EntityType == CatalogProductHistoryActions.EntityType
                && a.EntityId == productId);
        if (before is { } cursor) query = query.Where(a => a.CreatedAt < cursor);

        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows
            .Select(a => new CatalogProductHistoryEntryResponse(a.Id, a.Action, ParseChanges(a), a.ActorName, a.CreatedAt))
            .ToList();
    }

    internal static IReadOnlyList<CatalogProductHistoryChange> ParseChanges(CatalogAccessAuditEntry entry)
    {
        try
        {
            if (entry.Action == CatalogProductHistoryActions.AccessChanged) return AccessChanges(entry);
            if (string.IsNullOrWhiteSpace(entry.AfterJson)) return [];
            return JsonSerializer.Deserialize<StoredChanges>(entry.AfterJson, Json)?.Changes ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Legacy rows hold before/after snapshots of { visibility, customerIds, groupIds }.
    private static IReadOnlyList<CatalogProductHistoryChange> AccessChanges(CatalogAccessAuditEntry entry)
    {
        var (oldVisibility, oldCustomers, oldGroups) = ReadAccess(entry.BeforeJson);
        var (newVisibility, newCustomers, newGroups) = ReadAccess(entry.AfterJson);
        return new[]
        {
            CatalogProductHistoryDiff.Field("visibility", oldVisibility, newVisibility),
            CatalogProductHistoryDiff.Field("accessCustomers", oldCustomers?.ToString(), newCustomers?.ToString()),
            CatalogProductHistoryDiff.Field("accessGroups", oldGroups?.ToString(), newGroups?.ToString()),
        }.OfType<CatalogProductHistoryChange>().ToList();
    }

    private static (string? Visibility, int? Customers, int? Groups) ReadAccess(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null, null);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? visibility = root.TryGetProperty("visibility", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Count(string name) => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array ? e.GetArrayLength() : null;
        return (visibility, Count("customerIds"), Count("groupIds"));
    }

    private async Task<(Guid? Id, string? Name)> ResolveActorAsync(CancellationToken ct)
    {
        if (_actor is { } cached) return cached;
        var (_, actorId) = await CatalogOrderActorResolver.ResolveAsync(httpContextAccessor, dbContext, ct);
        string? name = null;
        if (actorId is not null)
        {
            name = await dbContext.Users.AsNoTracking()
                .Where(u => u.Id == actorId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(ct);
        }

        _actor = (actorId, name);
        return _actor.Value;
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    internal sealed record StoredChanges(IReadOnlyList<CatalogProductHistoryChange> Changes);
}
