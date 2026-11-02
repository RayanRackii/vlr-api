using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public sealed class PlanningVersionConflictArgumentException(string message) : ArgumentException(message)
{
    public const string Code = "planning_version_conflict";
}

public interface ICatalogProductResourceRequirementService
{
    Task<CatalogProductResourceRequirementsResponse> GetAsync(Guid productId, CancellationToken cancellationToken);

    Task<CatalogProductResourceRequirementsResponse> ReplaceAsync(
        Guid productId,
        ReplaceCatalogProductResourceRequirementsRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read/replace a product's resource-requirement configuration (order-equipment-link-codex §2).
/// A separate contract from product PUT so existing product-edit clients can never accidentally
/// clear requirements. Phase 0 only supports a specific RentalAssetId selector; AssetCategoryId is
/// accepted in the request shape (schema parity with the domain row) but rejected with 400.
/// </summary>
public sealed class CatalogProductResourceRequirementService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : ICatalogProductResourceRequirementService
{
    public async Task<CatalogProductResourceRequirementsResponse> GetAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var product = await dbContext.CatalogProducts
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId && p.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Product not found.");

        var requirements = await dbContext.CatalogProductResourceRequirements
            .AsNoTracking()
            .Where(r => r.ProductId == productId && r.TenantId == tenantId && r.IsActive)
            .ToListAsync(cancellationToken);

        return new CatalogProductResourceRequirementsResponse(
            product.ResourceRequirementsVersion,
            requirements.Select(ToDto).ToList());
    }

    public async Task<CatalogProductResourceRequirementsResponse> ReplaceAsync(
        Guid productId,
        ReplaceCatalogProductResourceRequirementsRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var product = await dbContext.CatalogProducts
            .FirstOrDefaultAsync(p => p.Id == productId && p.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Product not found.");

        // H5 Phase 0 decision: a product config may activate at most one requirement — batch
        // allocation across more than one line is not supported yet.
        if (request.Requirements.Count > 1)
        {
            throw new UnsupportedRequirementScopeException(
                "A product may have at most one active resource requirement; batch allocation is not supported yet.");
        }

        var rentalAssetIds = request.Requirements
            .Where(r => r.RentalAssetId is not null)
            .Select(r => r.RentalAssetId!.Value)
            .Distinct()
            .ToList();

        var validRentalAssetIds = rentalAssetIds.Count == 0
            ? []
            : await dbContext.RentalAssets
                .Where(r => r.TenantId == tenantId && rentalAssetIds.Contains(r.Id))
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
        var validRentalAssetIdSet = validRentalAssetIds.ToHashSet();

        foreach (var row in request.Requirements)
        {
            var hasRentalAsset = row.RentalAssetId is not null;
            var hasCategory = row.AssetCategoryId is not null;

            if (hasRentalAsset == hasCategory)
            {
                throw new ArgumentException(
                    "Exactly one of rentalAssetId or assetCategoryId must be set per requirement row.");
            }

            if (hasCategory)
            {
                // Phase 0 only supports a specific RentalAssetId selector; category selection is
                // accepted in schema but rejected by endpoints (order-equipment-link-codex Phase 0
                // scope: "category selection returns 400 'not supported yet'").
                throw new NotSupportedYetException();
            }

            if (row.RentalAssetId is { } rentalAssetId && !validRentalAssetIdSet.Contains(rentalAssetId))
            {
                // Cross-tenant or missing: 404, never leak existence via 403/400.
                throw new KeyNotFoundException($"Rental asset '{rentalAssetId}' was not found.");
            }

            if (row.ResourceQuantity < 1)
            {
                throw new ArgumentException("resourceQuantity must be at least 1.");
            }

            if (row.DurationMinutesPerUnit is < 1 or > CatalogPlanningWindow.MaxMinutes)
            {
                throw new ArgumentException("durationMinutesPerUnit must be between 1 and 1440.");
            }

            if (row.PrepMinutes < 0 || row.CleanupMinutes < 0)
            {
                throw new ArgumentException("prepMinutes and cleanupMinutes cannot be negative.");
            }
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            // M3: ExpectedVersion was read before this transaction/lock and CatalogProduct has no
            // concurrency token, so two concurrent replaces could otherwise both pass the same
            // check and both write an "active" set — lock the product row, reload its current
            // value under that lock, and only then compare. The loser gets a real 409, not a race.
            if (dbContext.Database.IsRelational())
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    SELECT 1 FROM catalog.catalog_products WHERE id = {productId} AND tenant_id = {tenantId} FOR UPDATE
                    """,
                    cancellationToken);
                await dbContext.Entry(product).ReloadAsync(cancellationToken);
            }

            if (request.ExpectedVersion != product.ResourceRequirementsVersion)
            {
                throw new PlanningVersionConflictArgumentException(
                    "The product's resource-requirements configuration changed since this edit was prepared; reload and retry.");
            }

            // Soft-deactivate the current active set rather than deleting, so historical order-item
            // snapshots that point at a SourceRequirementId keep resolving (Restrict delete behavior
            // on that FK would otherwise block this very call). The new version's rows are freshly
            // inserted; an empty `requirements: []` array is a valid, distinguishable-by-version
            // "no scheduling required going forward" state (see DTO remarks).
            var existing = await dbContext.CatalogProductResourceRequirements
                .Where(r => r.ProductId == productId && r.TenantId == tenantId && r.IsActive)
                .ToListAsync(cancellationToken);

            foreach (var row in existing)
            {
                row.IsActive = false;
                row.Touch();
            }

            var newVersion = product.ResourceRequirementsVersion + 1;
            foreach (var row in request.Requirements)
            {
                dbContext.CatalogProductResourceRequirements.Add(new CatalogProductResourceRequirement
                {
                    TenantId = tenantId,
                    ProductId = productId,
                    RentalAssetId = row.RentalAssetId,
                    AssetCategoryId = row.AssetCategoryId,
                    ResourceQuantity = row.ResourceQuantity,
                    DurationMinutesPerUnit = row.DurationMinutesPerUnit,
                    PrepMinutes = row.PrepMinutes,
                    CleanupMinutes = row.CleanupMinutes,
                    IsActive = true,
                    Version = newVersion,
                });
            }

            product.ResourceRequirementsVersion = newVersion;
            product.Touch();

            await dbContext.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return await GetAsync(productId, cancellationToken);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    private static CatalogProductResourceRequirementDto ToDto(CatalogProductResourceRequirement r) =>
        new()
        {
            Id = r.Id,
            RentalAssetId = r.RentalAssetId,
            AssetCategoryId = r.AssetCategoryId,
            ResourceQuantity = r.ResourceQuantity,
            DurationMinutesPerUnit = r.DurationMinutesPerUnit,
            PrepMinutes = r.PrepMinutes,
            CleanupMinutes = r.CleanupMinutes,
        };

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}

public sealed class NotSupportedYetException() : InvalidOperationException("not supported yet")
{
    public const string Code = "not_supported_yet";
}
