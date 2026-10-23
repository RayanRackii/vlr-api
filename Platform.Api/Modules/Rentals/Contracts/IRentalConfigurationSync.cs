using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Contracts;

public interface IRentalConfigurationSync
{
    Task AcquireBookingMapLockAsync(Guid tenantId, CancellationToken cancellationToken);

    int? NormalizeMaxAdvanceBookingDays(int? days);

    void Sync(Asset asset, RentalConfigurationSyncRequest request);

    void ApplyTemplateDefaults(Asset asset, ResourceVisualTemplateRevision? revision, bool apply);
}

public sealed record RentalConfigurationSyncRequest(
    bool IsRentable,
    RentalAssetType RentalType,
    int TotalQuantity,
    bool RequiresDeposit,
    bool QueueEnabled,
    TimeOnly? QueueOpeningTime);
