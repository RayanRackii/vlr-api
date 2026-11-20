using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.ResourcePlanning.Contracts;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogOrderAllocationPreviewService
{
    Task<CatalogOrderAllocationPreviewResponse> PreviewAsync(
        Guid orderId,
        CatalogOrderAllocationPreviewRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Read-only allocation preview: reserves nothing, holds no lock (order-equipment-link-codex §2 —
/// "Read-only; reserves nothing"). A preview is never a booking guarantee; by the time approval
/// actually runs, the same resource may have been claimed by another concurrent approval.
/// Goes through the planning port, like the approval it previews: the asset's calendar authority
/// (rentals for a rental-backed asset, planning for any other) decides the window rules and the
/// conflicts.
/// </summary>
public sealed class CatalogOrderAllocationPreviewService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IResourceOccupancyService occupancy,
    IAssetOccupancyService? assetOccupancyService = null) : ICatalogOrderAllocationPreviewService
{
    private readonly IAssetOccupancyService assetOccupancy =
        AssetOccupancyFactory.Create(assetOccupancyService, dbContext, tenantProvider, occupancy);

    public async Task<CatalogOrderAllocationPreviewResponse> PreviewAsync(
        Guid orderId,
        CatalogOrderAllocationPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();

        var order = await dbContext.CatalogOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken)
            ?? throw new KeyNotFoundException("Order not found.");

        var itemIds = order.Items.Select(i => i.Id).ToList();
        var requirements = await dbContext.CatalogOrderItemResourceRequirements
            .AsNoTracking()
            .Where(r => itemIds.Contains(r.OrderItemId))
            .ToListAsync(cancellationToken);

        // Phase 0 scope: "one scheduled line per order". More than one requirement snapshot on an
        // order is a data/logic bug from a later phase's perspective, not a crash here — preview
        // just evaluates the first one deterministically (lowest Id) and ignores the rest.
        var requirement = requirements.OrderBy(r => r.Id).FirstOrDefault();
        if (requirement is null)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, null, null, null, null, null, null, null,
                ConflictCode: null,
                Reason: "This order has no schedulable resource requirement.");
        }

        if (requirement.AssetId is null && requirement.RentalAssetId is null)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, null, null, null, null, null, null,
                ConflictCode: "not_supported_yet",
                Reason: "Category-based resource selection is not supported yet.");
        }

        var workStartRequest = request.WorkStarts.FirstOrDefault(w => w.OrderItemId == requirement.OrderItemId)
            ?? request.WorkStarts.FirstOrDefault();
        if (workStartRequest is null)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, requirement.RentalAssetId, null, null, null, null, null,
                ConflictCode: null,
                Reason: "A workStart is required for the schedulable order item.",
                AssetId: requirement.AssetId);
        }

        var assetId = await CatalogOrderPlanner.ResolveAssetIdAsync(
            dbContext, tenantId, requirement.AssetId, requirement.RentalAssetId, cancellationToken);

        PlannableAsset asset;
        try
        {
            asset = await CatalogOrderPlanner.ResolveAsync(
                assetOccupancy, occupancy, dbContext, tenantId, assetId, requirement.RentalAssetId, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, requirement.RentalAssetId, null, null, null, null, null,
                ConflictCode: null,
                Reason: "The configured resource no longer exists.",
                AssetId: requirement.AssetId);
        }
        catch (ResourceUnavailableException ex)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, requirement.RentalAssetId, null, null, null, null, null,
                ConflictCode: "resource_unavailable",
                Reason: ex.Message,
                AssetId: assetId == Guid.Empty ? requirement.AssetId : assetId);
        }

        // The rentals eligibility rules apply to a rental-backed asset, as in Phase 0.
        EligibleResource? eligibleResource = null;
        if (asset.RentalAssetId is { } rentalAssetId)
        {
            eligibleResource = await occupancy.ResolveEligibleResourceAsync(
                dbContext, tenantId, rentalAssetId, cancellationToken);
            if (eligibleResource is null)
            {
                return new CatalogOrderAllocationPreviewResponse(
                    IsFeasible: false, requirement.OrderItemId, rentalAssetId, null, null, null, null, null,
                    ConflictCode: null,
                    Reason: "The configured resource no longer exists.",
                    AssetId: asset.AssetId);
            }
        }

        var orderItem = order.Items.First(i => i.Id == requirement.OrderItemId);
        // Normalize to UTC at the boundary: a workStart carrying a non-zero offset (e.g. -03:00) is
        // a valid instant, but PostgreSQL's timestamptz parameters require offset 0 — without this
        // the downstream occupancy query would surface a raw driver error instead of a clean result.
        var workStart = workStartRequest.WorkStart.ToUniversalTime();
        var (workEnd, _, _) = CatalogPlanningWindow.Calculate(
            workStart, requirement.DurationMinutesPerUnit, orderItem.Quantity,
            requirement.PrepMinutes, requirement.CleanupMinutes, CatalogOrderPlanner.MaxWorkMinutes(asset));

        var window = await CatalogOrderPlanner.RunAsync(asset, () => assetOccupancy.ValidateWindowAsync(
            tenantId,
            asset,
            AssetAllocationKind.Use,
            workStart,
            workEnd,
            requirement.PrepMinutes,
            requirement.CleanupMinutes,
            cancellationToken));

        if (window.RentalWindowError is { } windowError)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, asset.RentalAssetId, workStart, workEnd, window.OccupiedStart, window.OccupiedEnd, null,
                ConflictCode: WorkWindowValidation.UnsupportedWorkWindowCode,
                Reason: windowError,
                AssetId: asset.AssetId);
        }

        if (window.HoursViolation is { } hoursViolation)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, asset.RentalAssetId, workStart, workEnd, window.OccupiedStart, window.OccupiedEnd, null,
                ConflictCode: "outside_working_hours",
                Reason: $"The work window is outside the working hours of '{asset.Name}' ({hoursViolation}).",
                AssetId: asset.AssetId);
        }

        if (eligibleResource is { IsEligible: false })
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, asset.RentalAssetId, workStart, workEnd, window.OccupiedStart, window.OccupiedEnd, null,
                ConflictCode: "resource_unavailable",
                Reason: eligibleResource.IneligibilityReason
                    ?? "The configured resource is not currently available for production use.",
                AssetId: asset.AssetId);
        }

        if (asset.RentalAssetId is null && requirement.ResourceQuantity > asset.Capacity)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, null, workStart, workEnd, window.OccupiedStart, window.OccupiedEnd, null,
                ConflictCode: "resource_unavailable",
                Reason: $"'{asset.Name}' cannot hold {requirement.ResourceQuantity} units at once.",
                AssetId: asset.AssetId);
        }

        var availability = await CatalogOrderPlanner.RunAsync(asset, () => assetOccupancy.CheckAvailabilityAsync(
            tenantId, asset, AssetAllocationKind.Use, window, excludeAllocationId: null, cancellationToken));

        if (!availability.IsFree)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, asset.RentalAssetId, workStart, workEnd, window.OccupiedStart, window.OccupiedEnd, null,
                ConflictCode: "resource_unavailable",
                Reason: $"'{asset.Name}' is already booked for an overlapping interval.",
                AssetId: asset.AssetId);
        }

        return new CatalogOrderAllocationPreviewResponse(
            IsFeasible: true,
            requirement.OrderItemId,
            asset.RentalAssetId,
            workStart,
            workEnd,
            window.OccupiedStart,
            window.OccupiedEnd,
            EstimatedReadyAt: workEnd,
            ConflictCode: null,
            Reason: null,
            AssetId: asset.AssetId);
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
