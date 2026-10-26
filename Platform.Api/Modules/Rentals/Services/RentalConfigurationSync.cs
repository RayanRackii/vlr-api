using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalConfigurationSync(AppDbContext dbContext) : IRentalConfigurationSync
{
    public Task AcquireBookingMapLockAsync(Guid tenantId, CancellationToken cancellationToken) =>
        BookingMapLocks.AcquireAsync(dbContext, tenantId, cancellationToken);

    public int? NormalizeMaxAdvanceBookingDays(int? days) =>
        BookingPolicyText.NormalizeAdvanceDays(days);

    public void Sync(Asset asset, RentalConfigurationSyncRequest request)
    {
        if (!request.IsRentable)
        {
            if (asset.RentalConfiguration is not null)
            {
                asset.RentalConfiguration.IsActive = false;
                asset.RentalConfiguration.Touch();
            }

            return;
        }

        var enabled = request.QueueEnabled && request.RentalType == RentalAssetType.Location;
        var openingTime = enabled ? request.QueueOpeningTime : null;

        if (asset.RentalConfiguration is null)
        {
            asset.RentalConfiguration = new RentalAsset
            {
                TenantId = asset.TenantId,
                AssetId = asset.Id,
                Type = request.RentalType,
                TotalQuantity = request.TotalQuantity,
                IsActive = true,
                RequiresDeposit = request.RequiresDeposit,
                SchedulePolicy = SchedulePolicy.SlotGrid,
                QueueEnabled = enabled,
                QueueOpeningTime = openingTime,
            };
            return;
        }

        asset.RentalConfiguration.Type = request.RentalType;
        asset.RentalConfiguration.TotalQuantity = request.TotalQuantity;
        asset.RentalConfiguration.RequiresDeposit = request.RequiresDeposit;
        asset.RentalConfiguration.QueueEnabled = enabled;
        asset.RentalConfiguration.QueueOpeningTime = openingTime;
        asset.RentalConfiguration.IsActive = true;
        asset.RentalConfiguration.Touch();
    }

    public void ApplyTemplateDefaults(Asset asset, ResourceVisualTemplateRevision? revision, bool apply)
    {
        if (!apply || revision is null || asset.RentalConfiguration is not { } rental)
        {
            return;
        }

        if (revision.DefaultSchedulePolicy is { } policy)
        {
            rental.SchedulePolicy = policy;
            rental.OpenTime = revision.DefaultOpenTime;
            rental.CloseTime = revision.DefaultCloseTime;
            rental.AllowedDurationMinutes = revision.DefaultAllowedDurationMinutes;
        }

        if (revision.DefaultPricings is not { Count: > 0 })
        {
            return;
        }

        var pricings = revision.DefaultPricings.Select(value => new CreateRentalPricingDto
        {
            DayOfWeek = value.DayOfWeek,
            StartTime = value.StartTime,
            EndTime = value.EndTime,
            PricePerHour = value.PricePerHour,
            PricingUnit = PricingUnit.Hour,
            RequiresDeposit = value.RequiresDeposit,
            DepositPercentage = value.DepositPercentage,
        }).ToArray();
        TemplateDefaultPricing.Validate(pricings);
        foreach (var pricing in pricings)
        {
            dbContext.RentalPricings.Add(new RentalPricing
            {
                TenantId = asset.TenantId,
                RentalAssetId = rental.Id,
                DayOfWeek = pricing.DayOfWeek,
                StartTime = pricing.StartTime,
                EndTime = pricing.EndTime,
                PricePerHour = Math.Round(pricing.PricePerHour, 2, MidpointRounding.AwayFromZero),
                PricingUnit = PricingUnit.Hour,
                RequiresDeposit = pricing.RequiresDeposit,
                DepositPercentage = Math.Round(pricing.DepositPercentage, 2, MidpointRounding.AwayFromZero),
            });
        }
    }
}
