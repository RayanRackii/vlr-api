using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ResourcePlanning.Contracts;

/// <summary>
/// Builds the planning port outside the container. The catalog services take the port as an optional
/// constructor argument (the container always supplies it); a caller that constructs them by hand,
/// as the Phase 0 tests do, gets the same implementation over its own context and rentals port. Lives
/// in the contract namespace so other modules never reference the service namespace.
/// </summary>
public static class AssetOccupancyFactory
{
    public static IAssetOccupancyService Create(
        IAssetOccupancyService? supplied,
        AppDbContext dbContext,
        ITenantProvider tenantProvider,
        IResourceOccupancyService rentalOccupancy) =>
        supplied ?? new AssetOccupancyService(
            dbContext,
            new TenantModuleAccessor(dbContext, tenantProvider),
            rentalOccupancy,
            TimeProvider.System);
}
