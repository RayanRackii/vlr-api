using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class SetupStatusService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : ISetupStatusService
{
    public async Task<SetupStatusResponse> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var now = DateTimeOffset.UtcNow;

        var tenant = await dbContext.Tenants.AsNoTracking()
            .FirstAsync(item => item.Id == tenantId, cancellationToken);
        var assets = await dbContext.RentalAssets.AsNoTracking()
            .Where(asset => asset.TenantId == tenantId)
            .Select(asset => new
            {
                asset.Id,
                asset.AssetId,
                asset.Type,
                asset.IsActive,
                asset.DirectRental,
                asset.OpenTime,
                asset.CloseTime,
                asset.QueueEnabled,
                asset.QueueOpeningTime,
            })
            .ToListAsync(cancellationToken);
        var names = await dbContext.Assets.AsNoTracking()
            .Where(asset => asset.TenantId == tenantId)
            .Select(asset => new { asset.Id, asset.Name })
            .ToListAsync(cancellationToken);
        var pricedAssets = await dbContext.RentalPricings.AsNoTracking()
            .Where(price => price.TenantId == tenantId && price.PricePerHour > 0)
            .Select(price => price.RentalAssetId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var templatedAssets = await dbContext.ScheduleTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.IsActive)
            .Select(template => template.RentalAssetId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var menus = await dbContext.TenantModuleMenuItems.AsNoTracking()
            .Where(item => item.TenantId == tenantId && item.IsActive)
            .Select(item => new { item.ModuleName, item.RentalAssetId })
            .ToListAsync(cancellationToken);
        var offerAssetIds = (await (
            from requirement in dbContext.CatalogProductResourceRequirements.AsNoTracking()
            join product in dbContext.CatalogProducts.AsNoTracking()
                on requirement.ProductId equals product.Id
            where requirement.TenantId == tenantId
                && requirement.IsActive
                && requirement.RentalAssetId != null
                && product.IsActive
                && product.Price > 0
            select requirement.RentalAssetId)
            .ToListAsync(cancellationToken))
            .OfType<Guid>()
            .Distinct()
            .ToList();
        var invites = await dbContext.UserInvites.AsNoTracking()
            .Where(invite => invite.TenantId == tenantId)
            .Select(invite => new { invite.AcceptedAt, invite.RevokedAt, invite.ExpiresAt })
            .ToListAsync(cancellationToken);
        var userCount = await dbContext.Users.AsNoTracking()
            .CountAsync(user => user.TenantId == tenantId, cancellationToken);

        var nameByAsset = names.ToDictionary(item => item.Id, item => item.Name);
        var priced = pricedAssets.ToHashSet();
        var templated = templatedAssets.ToHashSet();
        var offered = offerAssetIds.ToHashSet();
        var generalMenu = menus.Any(item =>
            item.ModuleName == PlatformModules.Rentals && item.RentalAssetId is null);
        var menuAssets = menus
            .Where(item => item.ModuleName == PlatformModules.Rentals && item.RentalAssetId is not null)
            .Select(item => item.RentalAssetId!.Value)
            .ToHashSet();

        var resources = new List<SetupResourceStateResponse>(assets.Count);
        var published = 0;
        foreach (var asset in assets)
        {
            var hasHours = (asset.OpenTime is not null && asset.CloseTime is not null) || templated.Contains(asset.Id);
            var hasPrice = (asset.DirectRental && priced.Contains(asset.Id)) || offered.Contains(asset.Id);
            var onMenu = generalMenu || menuAssets.Contains(asset.Id);
            var queueBlocked = asset.Type == RentalAssetType.Location
                && asset.QueueEnabled
                && asset.QueueOpeningTime is null;
            var (state, missing) = SetupStatusCalculator.Resource(new SetupStatusCalculator.ResourceInput(
                nameByAsset.TryGetValue(asset.AssetId, out var name) && !string.IsNullOrWhiteSpace(name),
                hasHours,
                hasPrice,
                queueBlocked,
                asset.IsActive,
                onMenu));
            if (state == SetupStatusCalculator.Published) published++;
            resources.Add(new SetupResourceStateResponse(asset.Id, state, missing));
        }

        var pendingInvite = invites.Any(invite =>
            invite.AcceptedAt is null && invite.RevokedAt is null && invite.ExpiresAt > now);
        var inviteAccepted = invites.Any(invite => invite.AcceptedAt is not null) || userCount > 1;
        var checklist = SetupStatusCalculator.Checklist(new SetupStatusCalculator.ChecklistInput(
            !string.IsNullOrWhiteSpace(tenant.LegalName)
                && !string.IsNullOrWhiteSpace(tenant.TaxId)
                && !string.IsNullOrWhiteSpace(tenant.TradeName),
            assets.Any(asset => (asset.DirectRental && priced.Contains(asset.Id)) || offered.Contains(asset.Id)),
            templated.Count > 0 || assets.Any(asset => asset.OpenTime is not null && asset.CloseTime is not null),
            assets.Count,
            published,
            pendingInvite,
            inviteAccepted));

        return new SetupStatusResponse(
            checklist.Select(item => new SetupChecklistItemResponse(item.Key, item.State)).ToArray(),
            resources);
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
