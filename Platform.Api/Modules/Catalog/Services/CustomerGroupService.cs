using Microsoft.EntityFrameworkCore;
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
}

/// <summary>
/// Staff management of customer groups and per-product visibility. Every id received from the
/// client is resolved against the current tenant (explicit TenantId check on top of the global
/// query filter); ids from another tenant are rejected with <see cref="ArgumentException"/> (400).
/// </summary>
public sealed class CustomerGroupService(AppDbContext dbContext, ITenantProvider tenantProvider) : ICustomerGroupService
{
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
        await dbContext.SaveChangesAsync(ct);
        return Map(group, []);
    }

    public async Task<CustomerGroupResponse?> UpdateAsync(Guid id, UpsertCustomerGroupRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var group = await FindGroupAsync(tenantId, id, ct);
        if (group is null) return null;
        var name = NormalizeName(request.Name);
        await EnsureUniqueNameAsync(tenantId, name, id, ct);
        group.Rename(name, Trim(request.Description));
        await dbContext.SaveChangesAsync(ct);
        return await MapAsync(tenantId, group, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var group = await FindGroupAsync(tenantId, id, ct);
        if (group is null) return false;
        dbContext.CustomerGroups.Remove(group); // members and access grants cascade
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<CustomerGroupResponse?> AddMembersAsync(Guid id, CustomerGroupMembersRequest request, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var group = await FindGroupAsync(tenantId, id, ct);
        if (group is null) return null;
        var customerIds = (request.CustomerIds ?? []).Distinct().ToList();
        await EnsureCustomersInTenantAsync(tenantId, customerIds, ct);
        var existing = await dbContext.CustomerGroupMembers
            .Where(m => m.TenantId == tenantId && m.GroupId == id && customerIds.Contains(m.CustomerId))
            .Select(m => m.CustomerId).ToListAsync(ct);
        foreach (var cid in customerIds.Except(existing))
        {
            dbContext.CustomerGroupMembers.Add(new CustomerGroupMember { TenantId = tenantId, GroupId = id, CustomerId = cid });
        }
        group.Touch();
        await dbContext.SaveChangesAsync(ct);
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

        var product = await dbContext.CatalogProducts
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId, ct);
        if (product is null) return null;

        var customerIds = (request.CustomerIds ?? []).Distinct().ToList();
        var groupIds = (request.GroupIds ?? []).Distinct().ToList();
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
        await dbContext.SaveChangesAsync(ct);
        return new CatalogProductAccessResponse(productId, visibility.ToString(), customerIds, groupIds);
    }

    private async Task<CatalogProductAccessResponse> MapAccessAsync(Guid tenantId, CatalogProduct product, CancellationToken ct)
    {
        var grants = await dbContext.CatalogProductAccess.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ProductId == product.Id).ToListAsync(ct);
        return new CatalogProductAccessResponse(
            product.Id,
            product.Visibility.ToString(),
            grants.Where(a => a.CustomerId != null).Select(a => a.CustomerId!.Value).ToList(),
            grants.Where(a => a.GroupId != null).Select(a => a.GroupId!.Value).ToList());
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
        if (trimmed.Length is 0 or > 120) throw new ArgumentException("Name is required (max 120 characters).");
        return trimmed;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Guid EnsureTenant() =>
        tenantProvider.TenantId ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
