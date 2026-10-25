using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalsReadinessService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IRentalsReadinessService
{
    public async Task<RentalsReadinessResponse> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var unitCount = await dbContext.Units.AsNoTracking()
            .CountAsync(unit => unit.TenantId == tenantId, cancellationToken);
        var rentableAssetCount = await dbContext.RentalAssets.AsNoTracking()
            .CountAsync(asset => asset.TenantId == tenantId && asset.IsActive, cancellationToken);
        var scheduleTemplateCount = await dbContext.ScheduleTemplates.AsNoTracking()
            .CountAsync(template => template.TenantId == tenantId && template.IsActive, cancellationToken);
        var hasStoredPolicy = await dbContext.BookingPolicies.AsNoTracking()
            .AnyAsync(policy => policy.TenantId == tenantId && policy.ResourceType == null && policy.AssetCategoryId == null, cancellationToken);
        var visualOrMapCount =
            await dbContext.RentalAssets.AsNoTracking()
                .CountAsync(asset => asset.TenantId == tenantId && asset.VisualTemplateRevisionId != null, cancellationToken)
            + await dbContext.RentalLayouts.AsNoTracking()
                .CountAsync(layout => layout.TenantId == tenantId && layout.IsActive, cancellationToken);

        var items = new RentalsReadinessItem[]
        {
            new("hasUnits", unitCount > 0, unitCount),
            new("hasRentableAssets", rentableAssetCount > 0, rentableAssetCount),
            new("hasScheduleTemplateOrRule", scheduleTemplateCount > 0, scheduleTemplateCount),
            // A missing row is the established unlimited default policy.
            new("hasBookingPolicy", true, hasStoredPolicy ? 1 : null),
            new("hasVisualOrMap", visualOrMapCount > 0, visualOrMapCount),
        };

        return new RentalsReadinessResponse(items.Take(4).All(item => item.Done), items);
    }

    private Guid RequireTenant()
    {
        if (tenantProvider.TenantId is not Guid tenantId || tenantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Tenant context is required.");
        }

        return tenantId;
    }
}
