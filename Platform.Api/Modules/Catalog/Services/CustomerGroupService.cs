using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Catalog.Dtos;
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
    IHttpContextAccessor? httpContextAccessor = null) : ICustomerGroupService
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
        return groups.Select(g => Map(g, members)).ToList();
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
        return Map(group, []);
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

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(ct)
            : null;
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
        await dbContext.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return after;
    }

    private async Task<CatalogProductAccessResponse> MapAccessAsync(Guid tenantId, CatalogProduct product, CancellationToken ct)
    {
        var grants = await dbContext.CatalogProductAccess.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ProductId == product.Id).ToListAsync(ct);
        return new CatalogProductAccessResponse(
            product.Id,
            product.Visibility.ToString(),
            grants.Where(a => a.CustomerId != null).Select(a => a.CustomerId!.Value).ToList(),
            grants.Where(a => a.GroupId != null).Select(a => a.GroupId!.Value).ToList())
        {
            RestrictedWithoutGrants = product.Visibility == CatalogProductVisibility.Restricted && grants.Count == 0,
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
        return Map(group, members);
    }

    private static CustomerGroupResponse Map(CustomerGroup g, IEnumerable<CustomerGroupMember> members) =>
        new(g.Id, g.Name, g.Description,
            members.Where(m => m.GroupId == g.Id).Select(m => m.CustomerId).OrderBy(x => x).ToList(),
            g.CreatedAt, g.UpdatedAt);

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
