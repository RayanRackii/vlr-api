using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.ResourcePlanning.Contracts;
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
    ITenantProvider tenantProvider,
    IProductHistoryRecorder? historyRecorder = null) : ICatalogProductResourceRequirementService
{
    private readonly IProductHistoryRecorder _history = historyRecorder ?? new CatalogProductHistoryService(dbContext, tenantProvider);

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
        var requestedAssetIds = request.Requirements
            .Where(r => r.AssetId is not null)
            .Select(r => r.AssetId!.Value)
            .Distinct()
            .ToList();

        // Legacy selector: the rental asset and the asset behind it.
        var rentalTargets = rentalAssetIds.Count == 0
            ? new List<RentalTarget>()
            : await dbContext.RentalAssets
                .Where(r => r.TenantId == tenantId && rentalAssetIds.Contains(r.Id))
                .Select(r => new RentalTarget(r.Id, r.AssetId))
                .ToListAsync(cancellationToken);
        var assetIdByRentalAssetId = rentalTargets.ToDictionary(r => r.Id, r => r.AssetId);

        // New selector: any asset of the tenant that is rental-backed or marked plannable. The asset
        // lookup carries the tenant id on top of the query filter; another tenant's id is a 404.
        var assetTargets = requestedAssetIds.Count == 0
            ? new List<AssetTarget>()
            : await dbContext.Assets
                .AsNoTracking()
                .Where(a => a.TenantId == tenantId && requestedAssetIds.Contains(a.Id))
                .Select(a => new AssetTarget(
                    a.Id,
                    a.RentalConfiguration != null ? (Guid?)a.RentalConfiguration.Id : null,
                    dbContext.AssetPlanningProfiles.Any(
                        p => p.TenantId == tenantId && p.AssetId == a.Id && p.IsPlannable)))
                .ToListAsync(cancellationToken);
        var assetTargetById = assetTargets.ToDictionary(a => a.Id);

        // The row as stored: the asset, plus the rental asset when the asset is rental-backed (kept
        // for the readers that predate the asset selector).
        var resolved = new List<(Guid AssetId, Guid? RentalAssetId, int MaxDurationMinutes)>();

        foreach (var row in request.Requirements)
        {
            var hasAsset = row.AssetId is not null;
            var hasRentalAsset = row.RentalAssetId is not null;
            var hasCategory = row.AssetCategoryId is not null;

            if (hasCategory)
            {
                // Category selection is accepted in schema but rejected by endpoints
                // (order-equipment-link-codex Phase 0 scope: "category selection returns 400 'not
                // supported yet'"), whatever else the row carries.
                if (hasAsset || hasRentalAsset)
                {
                    throw new ArgumentException(
                        "Exactly one of assetId, rentalAssetId or assetCategoryId must be set per requirement row.");
                }

                throw new NotSupportedYetException();
            }

            if (!hasAsset && !hasRentalAsset)
            {
                throw new ArgumentException(
                    "Exactly one of assetId, rentalAssetId or assetCategoryId must be set per requirement row.");
            }

            Guid assetId;
            Guid? rentalId = null;
            var maxDuration = CatalogPlanningWindow.MaxMinutes;

            if (row.RentalAssetId is { } rentalAssetId)
            {
                if (!assetIdByRentalAssetId.TryGetValue(rentalAssetId, out assetId))
                {
                    // Cross-tenant or missing: 404, never leak existence via 403/400.
                    throw new KeyNotFoundException($"Rental asset '{rentalAssetId}' was not found.");
                }

                rentalId = rentalAssetId;

                if (row.AssetId is { } both && both != assetId)
                {
                    throw new ArgumentException("assetId and rentalAssetId must name the same resource.");
                }
            }
            else
            {
                var requestedAssetId = row.AssetId!.Value;
                if (!assetTargetById.TryGetValue(requestedAssetId, out var target))
                {
                    // Cross-tenant or missing: 404, never leak existence via 403/400.
                    throw new KeyNotFoundException($"Asset '{requestedAssetId}' was not found.");
                }

                if (target.RentalAssetId is null && !target.IsPlannable)
                {
                    throw new ArgumentException(
                        $"Asset '{requestedAssetId}' is not plannable: mark it as plannable in resource planning first.");
                }

                assetId = target.Id;
                rentalId = target.RentalAssetId;

                // A planning-only asset takes jobs up to the planning limit; a rental-backed one
                // keeps the one-day booking-window limit.
                if (rentalId is null)
                {
                    maxDuration = AssetPlanningLimits.MaxPlanningOnlyDurationMinutes;
                }
            }

            resolved.Add((assetId, rentalId, maxDuration));

            if (row.ResourceQuantity < 1)
            {
                throw new ArgumentException("resourceQuantity must be at least 1.");
            }

            if (row.DurationMinutesPerUnit < 1 || row.DurationMinutesPerUnit > maxDuration)
            {
                throw new ArgumentException(
                    $"durationMinutesPerUnit must be between 1 and {maxDuration}.");
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
            for (var index = 0; index < request.Requirements.Count; index++)
            {
                var row = request.Requirements[index];
                var target = resolved[index];
                dbContext.CatalogProductResourceRequirements.Add(new CatalogProductResourceRequirement
                {
                    TenantId = tenantId,
                    ProductId = productId,
                    AssetId = target.AssetId,
                    RentalAssetId = target.RentalAssetId,
                    AssetCategoryId = null,
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

            // Names for the history diff, by asset id; a row that predates the asset selector
            // resolves its asset through the rental asset.
            var legacyRentalIds = existing
                .Where(r => r.AssetId == null)
                .Select(r => r.RentalAssetId)
                .OfType<Guid>()
                .Distinct()
                .ToList();
            var namedAssetIds = existing
                .Select(r => r.AssetId)
                .OfType<Guid>()
                .Concat(resolved.Select(r => r.AssetId))
                .Distinct()
                .ToList();
            var nameByAsset = namedAssetIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await dbContext.Assets.AsNoTracking()
                    .Where(a => a.TenantId == tenantId && namedAssetIds.Contains(a.Id))
                    .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);
            var nameByLegacyRental = legacyRentalIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await (from rental in dbContext.RentalAssets.AsNoTracking()
                         join asset in dbContext.Assets.AsNoTracking() on rental.AssetId equals asset.Id
                         where rental.TenantId == tenantId && legacyRentalIds.Contains(rental.Id)
                         select new { rental.Id, asset.Name })
                    .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
            string? NameOf(CatalogProductResourceRequirement r) =>
                r.AssetId is { } assetKey && nameByAsset.TryGetValue(assetKey, out var assetName) ? assetName
                : r.RentalAssetId is { } rentalKey && nameByLegacyRental.TryGetValue(rentalKey, out var rentalName) ? rentalName
                : null;
            string? NameOfAsset(Guid assetKey) => nameByAsset.TryGetValue(assetKey, out var name) ? name : null;
            var requirementChanges = CatalogProductHistoryDiff.ForRequirements(
                existing.Select(r => CatalogProductHistoryDiff.RequirementLine(
                    NameOf(r), r.ResourceQuantity, r.DurationMinutesPerUnit, r.PrepMinutes, r.CleanupMinutes)),
                request.Requirements.Select((r, i) => CatalogProductHistoryDiff.RequirementLine(
                    NameOfAsset(resolved[i].AssetId), r.ResourceQuantity, r.DurationMinutesPerUnit, r.PrepMinutes, r.CleanupMinutes)));
            if (requirementChanges.Count > 0)
                await _history.RecordAsync(productId, CatalogProductHistoryActions.RequirementsChanged, requirementChanges, cancellationToken);

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
            AssetId = r.AssetId,
            RentalAssetId = r.RentalAssetId,
            AssetCategoryId = r.AssetCategoryId,
            ResourceQuantity = r.ResourceQuantity,
            DurationMinutesPerUnit = r.DurationMinutesPerUnit,
            PrepMinutes = r.PrepMinutes,
            CleanupMinutes = r.CleanupMinutes,
        };

    private sealed record RentalTarget(Guid Id, Guid AssetId);

    private sealed record AssetTarget(Guid Id, Guid? RentalAssetId, bool IsPlannable);

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}

public sealed class NotSupportedYetException() : InvalidOperationException("not supported yet")
{
    public const string Code = "not_supported_yet";
}
