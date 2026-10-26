using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Services.Tenancy;

public sealed class TenantProvisioner(
    AppDbContext dbContext,
    ILogger<TenantProvisioner> logger) : ITenantProvisioner
{
    public async Task<TenantProvisioningResult> ProvisionAsync(
        Tenant tenant,
        string unitName,
        string? unitCode,
        IReadOnlyList<string> modules,
        IReadOnlyList<Guid> familyIds,
        CancellationToken cancellationToken)
    {
        using var scope = PlatformScope.Begin(logger, "tenant-provision");
        var headquarters = new Unit(tenant.Id, unitName, unitCode);
        dbContext.Tenants.Add(tenant);
        dbContext.Units.Add(headquarters);
        dbContext.BookingPolicies.Add(BookingPolicyDefaults.SuggestedFor(tenant.Id));

        var normalizedModules = PlatformModuleCatalog.NormalizeEntitlements(modules);
        foreach (var module in normalizedModules)
        {
            dbContext.TenantModules.Add(new TenantModule(tenant.Id, module, isActive: true));
        }

        // New tenant: no stored menu items yet.
        ModuleDefaultMenuItems.Ensure(dbContext, tenant.Id, normalizedModules, []);

        foreach (var familyId in familyIds)
        {
            dbContext.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, familyId));
        }

        await AssetCategoryExampleSeeder.SeedForFamilyIdsAsync(
            dbContext, tenant.Id, familyIds.ToList(), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        Role? trialSuperAdmin = null;
        if (tenant.IsTrial)
        {
            trialSuperAdmin = new Role(
                tenant.Id, SystemRoles.SuperAdmin,
                "Tenant super administrator with full access.", isSystemRole: true);
            dbContext.Roles.Add(trialSuperAdmin);
        }

        return new TenantProvisioningResult(headquarters, trialSuperAdmin);
    }
}
