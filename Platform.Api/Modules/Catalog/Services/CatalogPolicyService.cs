using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public sealed class CatalogPolicyService(AppDbContext dbContext, ITenantProvider tenantProvider)
{
    public async Task<CatalogPolicyResponse> GetAsync(CancellationToken cancellationToken) =>
        await ReadAsync(dbContext, RequireTenant(), cancellationToken);

    internal static async Task<CatalogPolicyResponse> ReadAsync(
        AppDbContext db, Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
            throw new UnauthorizedAccessException("Tenant context is required.");
        var policy = await db.CatalogPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);
        return policy is null
            ? new(true, CatalogOrderStatus.Requested, false, ProductRequestStatus.Submitted, null)
            : Map(policy);
    }

    public async Task<CatalogPolicyResponse> UpsertAsync(
        UpsertCatalogPolicyRequest request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        if (request.OrderCancelableUpToStatus is < CatalogOrderStatus.Requested or > CatalogOrderStatus.Ready)
            throw new ArgumentException("OrderCancelableUpToStatus deve ser Requested, Approved, Preparing ou Ready.");
        if (request.RequestCancelableUpToStatus != ProductRequestStatus.Submitted)
            throw new ArgumentException("RequestCancelableUpToStatus deve ser Submitted.");
        if (request.CancellationPolicyText is { Length: > CatalogPolicy.CancellationPolicyMaxLength })
            throw new ArgumentException("O texto da política deve ter no máximo 2000 caracteres.");

        var policy = await dbContext.CatalogPolicies.SingleOrDefaultAsync(p => p.TenantId == tenantId, cancellationToken);
        var inserted = policy is null;
        policy ??= new CatalogPolicy { TenantId = tenantId };
        if (inserted) dbContext.CatalogPolicies.Add(policy);
        Replace(policy, request);
        MarkFullReplace(policy);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (inserted && ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Concurrent first PUT: reuse the winning tenant row, never clear unrelated tracked changes.
            dbContext.Entry(policy).State = EntityState.Detached;
            policy = await dbContext.CatalogPolicies.SingleAsync(p => p.TenantId == tenantId, cancellationToken);
            Replace(policy, request);
            MarkFullReplace(policy);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return Map(policy);
    }

    private Guid RequireTenant() => tenantProvider.TenantId is Guid id && id != Guid.Empty
        ? id : throw new UnauthorizedAccessException("Tenant context is required.");

    // PUT is a full replacement: write every column so concurrent PUTs cannot merge into a mix no admin sent.
    private void MarkFullReplace(CatalogPolicy policy)
    {
        var entry = dbContext.Entry(policy);
        if (entry.State is EntityState.Added or EntityState.Detached) return;
        foreach (var property in entry.Properties)
            if (!property.Metadata.IsPrimaryKey()) property.IsModified = true;
    }

    private static void Replace(CatalogPolicy policy, UpsertCatalogPolicyRequest request)
    {
        policy.CustomerCanCancelOrders = request.CustomerCanCancelOrders;
        policy.OrderCancelableUpToStatus = request.OrderCancelableUpToStatus;
        policy.CustomerCanCancelRequests = request.CustomerCanCancelRequests;
        policy.RequestCancelableUpToStatus = request.RequestCancelableUpToStatus;
        policy.CancellationPolicyText = request.CancellationPolicyText;
        policy.Touch();
    }

    private static CatalogPolicyResponse Map(CatalogPolicy p) => new(
        p.CustomerCanCancelOrders, p.OrderCancelableUpToStatus,
        p.CustomerCanCancelRequests, p.RequestCancelableUpToStatus, p.CancellationPolicyText);
}
