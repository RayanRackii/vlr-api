using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Contracts;
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
/// </summary>
public sealed class CatalogOrderAllocationPreviewService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IResourceOccupancyService occupancy) : ICatalogOrderAllocationPreviewService
{
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

        if (requirement.RentalAssetId is not { } rentalAssetId)
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
                IsFeasible: false, requirement.OrderItemId, rentalAssetId, null, null, null, null, null,
                ConflictCode: null,
                Reason: "A workStart is required for the schedulable order item.");
        }

        var eligibleResource = await occupancy.ResolveEligibleResourceAsync(
            dbContext, tenantId, rentalAssetId, cancellationToken);
        if (eligibleResource is null)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, rentalAssetId, null, null, null, null, null,
                ConflictCode: null,
                Reason: "The configured resource no longer exists.");
        }

        var orderItem = order.Items.First(i => i.Id == requirement.OrderItemId);
        var workStart = workStartRequest.WorkStart;
        var workEnd = workStart + TimeSpan.FromMinutes((double)requirement.DurationMinutesPerUnit * orderItem.Quantity);
        var occupiedStart = workStart - TimeSpan.FromMinutes(requirement.PrepMinutes);
        var occupiedEnd = workEnd + TimeSpan.FromMinutes(requirement.CleanupMinutes);

        var windowValidation = await occupancy.ValidateWorkWindowAsync(
            dbContext, tenantId, rentalAssetId, occupiedStart, occupiedEnd, cancellationToken);
        if (windowValidation.Error is { } windowError)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, rentalAssetId, workStart, workEnd, occupiedStart, occupiedEnd, null,
                ConflictCode: WorkWindowValidation.UnsupportedWorkWindowCode,
                Reason: windowError);
        }

        if (!eligibleResource.IsEligible)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, rentalAssetId, workStart, workEnd, occupiedStart, occupiedEnd, null,
                ConflictCode: "resource_unavailable",
                Reason: eligibleResource.IneligibilityReason
                    ?? "The configured resource is not currently available for production use.");
        }

        var availability = await occupancy.CheckAvailabilityAsync(
            dbContext, tenantId, rentalAssetId, occupiedStart, occupiedEnd, tentativeBatch: [], cancellationToken);

        if (!availability.IsAvailable)
        {
            return new CatalogOrderAllocationPreviewResponse(
                IsFeasible: false, requirement.OrderItemId, rentalAssetId, workStart, workEnd, occupiedStart, occupiedEnd, null,
                ConflictCode: "resource_unavailable",
                Reason: availability.Reason);
        }

        return new CatalogOrderAllocationPreviewResponse(
            IsFeasible: true,
            requirement.OrderItemId,
            rentalAssetId,
            workStart,
            workEnd,
            occupiedStart,
            occupiedEnd,
            EstimatedReadyAt: workEnd,
            ConflictCode: null,
            Reason: null);
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
