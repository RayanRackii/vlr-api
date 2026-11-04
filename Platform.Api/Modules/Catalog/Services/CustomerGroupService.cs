using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICustomerGroupService
{
    Task<IReadOnlyList<CustomerGroupResponse>> ListAsync(CancellationToken ct);
    Task<CustomerGroupResponse?> GetAsync(Guid id, CancellationToken ct);
    Task<CustomerGroupResponse> CreateAsync(UpsertCustomerGroupRequest request, CancellationToken ct);
    Task<CustomerGroupResponse?> UpdateAsync(Guid id, UpsertCustomerGroupRequest request, CancellationToken ct);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct);
    Task<CustomerGroupResponse?> AddMembersAsync(Guid id, CustomerGroupMembersRequest request, CancellationToken ct);
    Task<CustomerGroupResponse?> RemoveMemberAsync(Guid id, Guid customerId, CancellationToken ct);
    Task<CatalogProductAccessResponse?> GetProductAccessAsync(Guid productId, CancellationToken ct);
    Task<CatalogProductAccessResponse?> SetProductAccessAsync(Guid productId, UpdateCatalogProductAccessRequest request, CancellationToken ct);
    Task<IReadOnlyList<CatalogAccessAuditResponse>> ListHistoryAsync(string entityType, Guid entityId, CancellationToken ct);
}

/// <summary>
/// Staff management of customer groups and per-product visibility. Every id received from the
/// client is resolved against the current tenant (explicit TenantId check on top of the global
/// query filter); ids from another tenant are rejected with <see cref="ArgumentException"/> (400).
/// </summary>
public sealed class CustomerGroupService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IHttpContextAccessor? httpContextAccessor = null,
    IStorageProvider? storageProvider = null,
    IOptions<StorageOptions>? storageOptions = null,
    ILogger<CustomerGroupService>? logger = null) : ICustomerGroupService
{
    public const int MaxIdsPerRequest = 500;
    public const int NameMaxLength = 200;

    public async Task<IReadOnlyList<CustomerGroupResponse>> ListAsync(CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var groups = await dbContext.CustomerGroups.AsNoTracking()
            .Where(g => g.TenantId == tenantId).OrderBy(g => g.Name).ToListAsync(ct);
        var ids = groups.Select(g => g.Id).ToList();
        var members = await dbContext.CustomerGroupMembers.AsNoTracking()
            .Where(m => m.TenantId == tenantId && ids.Contains(m.GroupId)).ToListAsync(ct);
        var names = await CustomerNamesAsync(tenantId, members.Select(m => m.CustomerId), ct);
        return groups.Select(g => Map(g, members, names)).ToList();
    }

    public async Task<CustomerGroupResponse?> GetAsync(Guid id, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var group = await FindGroupAsync(tenantId, id, ct);
        return group is null ? null : await MapAsync(tenantId, group, ct);
    }

    public async Task<CustomerGroupResponse> CreateAsync(UpsertCustomerGroupRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var name = NormalizeName(request.Name);
        await EnsureUniqueNameAsync(tenantId, name, null, ct);
        var group = new CustomerGroup { TenantId = tenantId, Name = name, Description = Trim(request.Description) };
        dbContext.CustomerGroups.Add(group);
        await AuditAsync(tenantId, "group.created", "group", group.Id, null, new { group.Name, group.Description }, ct);
        await SaveAsync(ct);
        return Map(group, [], new Dictionary<Guid, string>());
    }

    public async Task<CustomerGroupResponse?> UpdateAsync(Guid id, UpsertCustomerGroupRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var group = await FindGroupAsync(tenantId, id, ct);
        if (group is null) return null;
        var name = NormalizeName(request.Name);
        await EnsureUniqueNameAsync(tenantId, name, id, ct);
        var before = new { group.Name, group.Description };
        group.Rename(name, Trim(request.Description));
        await AuditAsync(tenantId, "group.updated", "group", id, before, new { group.Name, group.Description }, ct);
        await SaveAsync(ct);
        return await MapAsync(tenantId, group, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var group = await FindGroupAsync(tenantId, id, ct);
        if (group is null) return false;
        var memberIds = await dbContext.CustomerGroupMembers
            .Where(m => m.TenantId == tenantId && m.GroupId == id).Select(m => m.CustomerId).ToListAsync(ct);
        dbContext.CustomerGroups.Remove(group); // members and access grants cascade
        await AuditAsync(tenantId, "group.deleted", "group", id, new { group.Name, MemberCustomerIds = memberIds }, null, ct);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<CustomerGroupResponse?> AddMembersAsync(Guid id, CustomerGroupMembersRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var group = await FindGroupAsync(tenantId, id, ct);
        if (group is null) return null;
        var customerIds = Capped(request.CustomerIds, "customerIds");
        await EnsureCustomersInTenantAsync(tenantId, customerIds, ct);
        var existing = await dbContext.CustomerGroupMembers
            .Where(m => m.TenantId == tenantId && m.GroupId == id && customerIds.Contains(m.CustomerId))
            .Select(m => m.CustomerId).ToListAsync(ct);
        var added = customerIds.Except(existing).ToList();
        foreach (var cid in added)
        {
            dbContext.CustomerGroupMembers.Add(new CustomerGroupMember { TenantId = tenantId, GroupId = id, CustomerId = cid });
        }
        if (added.Count > 0)
        {
            group.Touch();
            await AuditAsync(tenantId, "group.membersAdded", "group", id, null, new { CustomerIds = added }, ct);
            await SaveAsync(ct);
        }
        return await MapAsync(tenantId, group, ct);
    }

    public async Task<CustomerGroupResponse?> RemoveMemberAsync(Guid id, Guid customerId, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var group = await FindGroupAsync(tenantId, id, ct);
        if (group is null) return null;
        var member = await dbContext.CustomerGroupMembers
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.GroupId == id && m.CustomerId == customerId, ct);
        if (member is not null)
        {
            dbContext.CustomerGroupMembers.Remove(member);
            group.Touch();
            await AuditAsync(tenantId, "group.memberRemoved", "group", id, new { CustomerId = customerId }, null, ct);
            await dbContext.SaveChangesAsync(ct);
        }
        return await MapAsync(tenantId, group, ct);
    }

    public async Task<CatalogProductAccessResponse?> GetProductAccessAsync(Guid productId, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var product = await dbContext.CatalogProducts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId, ct);
        return product is null ? null : await MapAccessAsync(tenantId, product, ct);
    }

    public async Task<CatalogProductAccessResponse?> SetProductAccessAsync(
        Guid productId, UpdateCatalogProductAccessRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        if (!Enum.TryParse<CatalogProductVisibility>(request.Visibility, ignoreCase: true, out var visibility)
            || !Enum.IsDefined(visibility))
        {
            throw new ArgumentException("Visibility must be Public or Restricted.");
        }

        var customerIds = Capped(request.CustomerIds, "customerIds");
        var groupIds = Capped(request.GroupIds, "groupIds");

        var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                // Serialize concurrent access edits on the same product.
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM catalog.catalog_products WHERE tenant_id = {tenantId} AND id = {productId} FOR UPDATE", ct);
            }

            var product = await dbContext.CatalogProducts
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId, ct);
            if (product is null) return null;
            var before = await MapAccessAsync(tenantId, product, ct);

            await EnsureCustomersInTenantAsync(tenantId, customerIds, ct);
            var validGroups = await dbContext.CustomerGroups
                .CountAsync(g => g.TenantId == tenantId && groupIds.Contains(g.Id), ct);
            if (validGroups != groupIds.Count)
            {
                throw new ArgumentException("One or more groups do not exist in this tenant.");
            }

            var moves = await PlanFileMovesAsync(tenantId, product, visibility, ct);

            product.Visibility = visibility;
            product.Touch();
            var current = await dbContext.CatalogProductAccess
                .Where(a => a.TenantId == tenantId && a.ProductId == productId).ToListAsync(ct);
            dbContext.CatalogProductAccess.RemoveRange(current);
            foreach (var cid in customerIds)
            {
                dbContext.CatalogProductAccess.Add(new CatalogProductAccess { TenantId = tenantId, ProductId = productId, CustomerId = cid });
            }
            foreach (var gid in groupIds)
            {
                dbContext.CatalogProductAccess.Add(new CatalogProductAccess { TenantId = tenantId, ProductId = productId, GroupId = gid });
            }
            var after = new CatalogProductAccessResponse(productId, visibility.ToString(), customerIds, groupIds)
            {
                RestrictedWithoutGrants = visibility == CatalogProductVisibility.Restricted && customerIds.Count + groupIds.Count == 0,
            };
            await AuditAsync(tenantId, "product.accessChanged", "product", productId, before, after, ct);

            // Fail-closed, ordered file moves between the public and private buckets. The DB commit is
            // the single pivot: a Restricted product must never persist with a reachable public file.
            var needsRevertToPublic = visibility == CatalogProductVisibility.Restricted
                ? await RestrictAsync(tenantId, productId, moves, transaction, ct)
                : await OpenAsync(tenantId, productId, moves, transaction, ct);

            // The commit happened (or was abandoned) inside Restrict/OpenAsync; the transaction is
            // complete. Dispose it before any further write (e.g. the revert) so EF does not try to
            // enlist in the finished transaction.
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
                transaction = null;
            }

            if (needsRevertToPublic)
            {
                // Recovery must complete even if the HTTP request was cancelled after the commit.
                await RevertToPublicAsync(tenantId, productId, CancellationToken.None);
                await RestorePublicCopiesAsync(moves);
                throw new InvalidOperationException(
                    "Could not remove every public copy while restricting; the product was reverted to Public. Please retry.");
            }

            return after with { CustomerNames = await CustomerNamesAsync(tenantId, customerIds, ct) };
        }
        catch
        {
            // Do not leave an uncommitted visibility/grant change tracked for a later SaveChanges.
            dbContext.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (transaction is not null && dbContext.Database.CurrentTransaction == transaction)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Público -> Restrito. (1) copy every public file to private and verify; a failure leaves the DB
    /// Public with the public files intact. (2) commit the switch to Restricted (the pivot). (3) delete
    /// the public copies LAST, idempotent, each confirmed gone with ExistsAsync and never deleting the
    /// sole copy. Returns true when a public copy could not be confirmed removed, so the caller reverts
    /// the product to Public — we never leave a Restricted product with a reachable public file.
    /// </summary>
    private async Task<bool> RestrictAsync(
        Guid tenantId, Guid productId, IReadOnlyList<FileMove> moves, IDbContextTransaction? transaction, CancellationToken ct)
    {
        foreach (var m in moves)
        {
            await CopyVerifiedAsync(m, ct);
        }

        await CommitAsync(tenantId, productId, transaction, CatalogProductVisibility.Restricted, ct);

        var anyUnconfirmed = false;
        foreach (var m in moves)
        {
            if (!await ConfirmSourceRemovedAsync(m, CancellationToken.None))
            {
                anyUnconfirmed = true;
                break;
            }
        }
        return anyUnconfirmed;
    }

    /// <summary>Deletes one source copy and confirms it is gone. The destination must exist first so we
    /// never delete the last remaining copy. The delete may time out after the object was already
    /// removed, so success is decided by a follow-up ExistsAsync rather than by the delete call.</summary>
    private async Task<bool> ConfirmSourceRemovedAsync(FileMove m, CancellationToken ct)
    {
        try
        {
            if (!await storageProvider!.ExistsAsync(m.FromBucket, m.Key, ct)) return true;
            if (!await storageProvider!.ExistsAsync(m.ToBucket, m.Key, ct)) return false;
            try
            {
                await storageProvider!.DeleteRequiredAsync(m.FromBucket, m.Key, ct);
            }
            catch (Exception)
            {
                // May be a timeout after the object was already removed; the ExistsAsync below decides.
            }
            return !await storageProvider!.ExistsAsync(m.FromBucket, m.Key, ct);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task CopyVerifiedAsync(FileMove move, CancellationToken ct)
    {
        if (await storageProvider!.ExistsAsync(move.ToBucket, move.Key, ct)) return;
        try
        {
            await storageProvider.CopyAsync(move.FromBucket, move.Key, move.ToBucket, move.Key, ct);
        }
        catch
        {
            // A timeout may follow a successful copy. Verify with a token independent of the request.
            if (!await storageProvider.ExistsAsync(move.ToBucket, move.Key, CancellationToken.None)) throw;
            return;
        }
        if (!await storageProvider.ExistsAsync(move.ToBucket, move.Key, ct))
            throw new InvalidOperationException("Storage copy could not be verified; please retry.");
    }

    private async Task RestorePublicCopiesAsync(IReadOnlyList<FileMove> moves)
    {
        var failed = false;
        foreach (var move in moves)
        {
            try
            {
                await CopyVerifiedAsync(new FileMove(move.ToBucket, move.FromBucket, move.Key), CancellationToken.None);
            }
            catch
            {
                failed = true;
                logger?.LogError("Product reverted to Public but public copy restoration failed for key {Key}; private copy retained", move.Key);
            }
        }
        if (failed)
            throw new InvalidOperationException("Product reverted to Public, but some public files could not be restored. Private copies were retained; please retry.");
    }

    /// <summary>
    /// Restrito -> Publico. Copy each private file to public and verify, then commit the switch to
    /// Public, then delete the private copies LAST (private leftovers are harmless). Pre-commit
    /// failures attempt verified public-copy cleanup; failed or ambiguous cleanup recovers Public
    /// even if restoring public availability fails, always retaining private files.
    /// </summary>
    private async Task<bool> OpenAsync(
        Guid tenantId, Guid productId, IReadOnlyList<FileMove> moves, IDbContextTransaction? transaction, CancellationToken ct)
    {
        var publicCopies = new List<FileMove>();
        try
        {
            foreach (var m in moves)
            {
                // Track before the call: a timeout may land the public object anyway, and the rollback
                // below must still remove it.
                publicCopies.Add(m);
                await CopyVerifiedAsync(m, ct);
            }
            await CommitAsync(tenantId, productId, transaction, CatalogProductVisibility.Public, ct);
        }
        catch
        {
            // Only remove destinations when the committed product is known to be Restricted.
            // An unreadable commit outcome must preserve copies needed by a possibly Public product.
            var state = await ReadOpeningVisibilityAsync(tenantId, productId);
            var publicCleanupFailed = false;
            if (state == CatalogProductVisibility.Restricted)
            {
                foreach (var move in publicCopies)
                {
                    if (!await ConfirmSourceRemovedAsync(new FileMove(move.ToBucket, move.FromBucket, move.Key), CancellationToken.None))
                        publicCleanupFailed = true;
                }
                if (publicCleanupFailed) state = await ReadOpeningVisibilityAsync(tenantId, productId);
            }
            if ((publicCleanupFailed || state is null) && state != CatalogProductVisibility.Public)
            {
                // Abandon the opening transaction before recovery writes: it may still hold the row
                // lock or contain uncommitted grants. The outer owner skips this disposed transaction.
                if (transaction is not null) await transaction.DisposeAsync();
                foreach (var move in moves)
                {
                    try
                    {
                        await CopyVerifiedAsync(move, CancellationToken.None);
                    }
                    catch
                    {
                        logger?.LogError(
                            "Opening recovery could not verify public copy for key {Key}; private copy retained", move.Key);
                    }
                }
                // Even degraded availability is safer than Restricted with a reachable public object.
                await RevertToPublicAsync(tenantId, productId, CancellationToken.None, "product.openRecovery");
                logger?.LogError(
                    "Opening failed and public cleanup could not be confirmed for product {ProductId}; recovered Public and retained private copies", productId);
            }
            throw;
        }
        var cleanupFailed = false;
        foreach (var move in moves)
            if (!await ConfirmSourceRemovedAsync(move, CancellationToken.None)) cleanupFailed = true;
        if (cleanupFailed)
            throw new InvalidOperationException("Product is Public, but private copy cleanup failed. Please retry.");
        return false;
    }

    private async Task<CatalogProductVisibility?> ReadOpeningVisibilityAsync(Guid tenantId, Guid productId)
    {
        try
        {
            return await ReadCommittedVisibilityAsync(tenantId, productId, CancellationToken.None);
        }
        catch
        {
            logger?.LogError("Opening recovery could not read committed visibility for product {ProductId}", productId);
            return null;
        }
    }

    /// <summary>Commits the pending DB changes. If the commit result is ambiguous (e.g. a timeout after
    /// the server applied it), re-reads the committed visibility on a fresh connection: if it already
    /// matches the target the switch landed and we proceed, otherwise the commit failed and we rethrow.
    /// Never disposes the transaction (the caller owns disposal).</summary>
    private async Task CommitAsync(
        Guid tenantId, Guid productId, IDbContextTransaction? transaction, CatalogProductVisibility target, CancellationToken ct)
    {
        try
        {
            await dbContext.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        catch
        {
            if (await ReadCommittedVisibilityAsync(tenantId, productId, CancellationToken.None) != target) throw;
            // The commit actually landed despite the ambiguous failure; treat it as success. For a
            // restriction this means we proceed to delete the public copies and never restore them.
        }
    }

    /// <summary>Reads the committed visibility outside the current (possibly broken) transaction, using a
    /// fresh connection for relational providers so a plain read is not blocked by the row lock.</summary>
    private async Task<CatalogProductVisibility?> ReadCommittedVisibilityAsync(Guid tenantId, Guid productId, CancellationToken ct)
    {
        if (!dbContext.Database.IsRelational())
        {
            dbContext.ChangeTracker.Clear();
            var row = await dbContext.CatalogProducts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId, ct);
            return row?.Visibility;
        }
        // Clone retains authentication details even when Persist Security Info is false, as well as
        // data-source authentication callbacks. Do not reconstruct from a sanitized connection string.
        await using var connection = (NpgsqlConnection)((ICloneable)dbContext.Database.GetDbConnection()).Clone();
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT visibility FROM catalog.catalog_products WHERE tenant_id = @tenant AND id = @id";
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("id", productId);
        var value = await command.ExecuteScalarAsync(ct) as string;
        return value is not null && Enum.TryParse<CatalogProductVisibility>(value, out var parsed) ? parsed : null;
    }

    /// <summary>Reverts a just-committed restriction back to Public when a public copy could not be
    /// removed. Grants stay stored (ignored by the portal while Public); private copies are kept.</summary>
    private async Task RevertToPublicAsync(Guid tenantId, Guid productId, CancellationToken ct,
        string auditAction = "product.restrictReverted")
    {
        dbContext.ChangeTracker.Clear();
        var product = await dbContext.CatalogProducts
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId, ct);
        if (product is null) throw new InvalidOperationException("Product disappeared during visibility recovery; public files were not restored.");
        product.Visibility = CatalogProductVisibility.Public;
        product.Touch();
        await AuditAsync(tenantId, auditAction, "product", productId, null,
            new { Reason = "public copy cleanup failed" }, ct);
        await CommitAsync(tenantId, productId, null, CatalogProductVisibility.Public, ct);
    }

    private sealed record FileMove(string FromBucket, string ToBucket, string Key);

    private async Task<IReadOnlyList<FileMove>> PlanFileMovesAsync(
        Guid tenantId, CatalogProduct product, CatalogProductVisibility target, CancellationToken ct)
    {
        if (storageProvider is null || storageOptions is null || product.Visibility == target) return [];
        var keys = await dbContext.CatalogProductFiles.AsNoTracking()
            .Where(f => f.TenantId == tenantId && f.ProductId == product.Id && f.Visibility == CatalogFileVisibility.CustomerVisible)
            .Select(f => f.StorageKey)
            .ToListAsync(ct);
        var from = Platform.Api.Modules.Catalog.CatalogFileBuckets.For(storageOptions.Value, product.Visibility, CatalogFileVisibility.CustomerVisible);
        var to = Platform.Api.Modules.Catalog.CatalogFileBuckets.For(storageOptions.Value, target, CatalogFileVisibility.CustomerVisible);
        return from == to ? [] : keys.Select(k => new FileMove(from, to, k)).ToList();
    }

    private async Task<CatalogProductAccessResponse> MapAccessAsync(Guid tenantId, CatalogProduct product, CancellationToken ct)
    {
        var grants = await dbContext.CatalogProductAccess.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ProductId == product.Id).ToListAsync(ct);
        var names = await CustomerNamesAsync(tenantId, grants.Where(a => a.CustomerId != null).Select(a => a.CustomerId!.Value), ct);
        return new CatalogProductAccessResponse(
            product.Id,
            product.Visibility.ToString(),
            grants.Where(a => a.CustomerId != null).Select(a => a.CustomerId!.Value).ToList(),
            grants.Where(a => a.GroupId != null).Select(a => a.GroupId!.Value).ToList())
        {
            RestrictedWithoutGrants = product.Visibility == CatalogProductVisibility.Restricted && grants.Count == 0,
            CustomerNames = names,
        };
    }

    public async Task<IReadOnlyList<CatalogAccessAuditResponse>> ListHistoryAsync(string entityType, Guid entityId, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        if (entityType is not ("group" or "product")) throw new ArgumentException("entityType must be group or product.");
        return await dbContext.CatalogAccessAudit.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(200)
            .Select(a => new CatalogAccessAuditResponse(a.Id, a.Action, a.EntityType, a.EntityId,
                a.ActorUserId == null ? "System" : "B2BUser", a.ActorUserId, a.ActorName, a.BeforeJson, a.AfterJson, a.CreatedAt))
            .ToListAsync(ct);
    }

    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    private async Task AuditAsync(Guid tenantId, string action, string entityType, Guid entityId, object? before, object? after, CancellationToken ct)
    {
        var (_, actorId) = await CatalogOrderActorResolver.ResolveAsync(httpContextAccessor, dbContext, ct);
        string? actorName = null;
        if (actorId is not null)
        {
            actorName = await dbContext.Users.AsNoTracking().Where(u => u.Id == actorId).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        }
        dbContext.CatalogAccessAudit.Add(new CatalogAccessAuditEntry
        {
            TenantId = tenantId,
            ActorUserId = actorId,
            ActorName = actorName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, AuditJson),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, AuditJson),
        });
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new InvalidOperationException("A group with this name already exists.");
        }
    }

    private static List<Guid> Capped(IReadOnlyList<Guid>? ids, string field)
    {
        var list = (ids ?? []).Distinct().ToList();
        if (list.Count > MaxIdsPerRequest) throw new ArgumentException($"{field} accepts at most {MaxIdsPerRequest} ids.");
        return list;
    }

    private async Task EnsureCustomersInTenantAsync(Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken ct)
    {
        if (customerIds.Count == 0) return;
        var found = await dbContext.Customers.CountAsync(c => c.TenantId == tenantId && customerIds.Contains(c.Id), ct);
        if (found != customerIds.Count)
        {
            throw new ArgumentException("One or more customers do not exist in this tenant.");
        }
    }

    private async Task EnsureUniqueNameAsync(Guid tenantId, string name, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLower();
        if (await dbContext.CustomerGroups.AnyAsync(
                g => g.TenantId == tenantId && g.Name.ToLower() == lower && (exceptId == null || g.Id != exceptId), ct))
        {
            throw new InvalidOperationException("A group with this name already exists.");
        }
    }

    private Task<CustomerGroup?> FindGroupAsync(Guid tenantId, Guid id, CancellationToken ct) =>
        dbContext.CustomerGroups.FirstOrDefaultAsync(g => g.TenantId == tenantId && g.Id == id, ct);

    private async Task<CustomerGroupResponse> MapAsync(Guid tenantId, CustomerGroup group, CancellationToken ct)
    {
        var members = await dbContext.CustomerGroupMembers.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.GroupId == group.Id).ToListAsync(ct);
        var names = await CustomerNamesAsync(tenantId, members.Select(m => m.CustomerId), ct);
        return Map(group, members, names);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> CustomerNamesAsync(Guid tenantId, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new Dictionary<Guid, string>();
        return await dbContext.Customers.AsNoTracking()
            .Where(c => c.TenantId == tenantId && list.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
    }

    private static CustomerGroupResponse Map(CustomerGroup g, IEnumerable<CustomerGroupMember> members, IReadOnlyDictionary<Guid, string> names)
    {
        var ids = members.Where(m => m.GroupId == g.Id).Select(m => m.CustomerId).OrderBy(x => x).ToList();
        return new(g.Id, g.Name, g.Description, ids, g.CreatedAt, g.UpdatedAt)
        {
            MemberNames = ids.Where(names.ContainsKey).ToDictionary(id => id, id => names[id]),
        };
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed.Length > NameMaxLength) throw new ArgumentException($"Name is required (max {NameMaxLength} characters).");
        return trimmed;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Guid EnsureTenant() =>
        tenantProvider.TenantId ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
