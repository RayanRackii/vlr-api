using Platform.Api.Authorization;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>One seeded tenant with one plannable asset.</summary>
internal sealed record PlanningTenantSeed(Guid TenantId, Guid UnitId, Guid AssetId, Guid SecondAssetId);

/// <summary>Seeds shared by the allocation tests (in-memory and PostgreSQL).</summary>
internal static class PlanningTestData
{
    /// <summary>
    /// Adds a tenant, a unit, a category, a family and two assets, both plannable under
    /// <paramref name="policy"/> with <paramref name="capacity"/>. The caller saves.
    /// </summary>
    public static PlanningTenantSeed AddTenant(
        AppDbContext db,
        string name = "Print Shop",
        AssetHoursPolicy policy = AssetHoursPolicy.Anytime,
        int capacity = 1,
        bool withPlanningModule = true,
        bool withRentalsModule = false,
        int prepMinutes = 0,
        int cleanupMinutes = 0)
    {
        var tenant = new Tenant(name, Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(), subdomain: $"plan-{Guid.NewGuid():N}"[..16]);
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Printers" };
        var family = new AssetFamily
        {
            Key = $"equip-{Guid.NewGuid():N}"[..32],
            Label = "Equipment",
            FieldSchemaJson = "{}",
        };
        var first = NewAsset(tenant.Id, unit.Id, category.Id, family.Id, "Printer A", "P1");
        var second = NewAsset(tenant.Id, unit.Id, category.Id, family.Id, "Printer B", "P2");

        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.AddRange(first, second);
        foreach (var asset in new[] { first, second })
        {
            db.AssetPlanningProfiles.Add(new AssetPlanningProfile
            {
                TenantId = tenant.Id,
                AssetId = asset.Id,
                IsPlannable = true,
                Capacity = capacity,
                HoursPolicy = policy,
                DefaultPrepMinutes = prepMinutes,
                DefaultCleanupMinutes = cleanupMinutes,
            });
        }

        if (withPlanningModule)
        {
            db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.ResourcePlanning, isActive: true));
        }

        if (withRentalsModule)
        {
            db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Rentals, isActive: true));
        }

        return new PlanningTenantSeed(tenant.Id, unit.Id, first.Id, second.Id);
    }

    public static Asset NewAsset(Guid tenantId, Guid unitId, Guid categoryId, Guid familyId, string name, string tag) =>
        new()
        {
            TenantId = tenantId,
            UnitId = unitId,
            CategoryId = categoryId,
            FamilyId = familyId,
            Name = name,
            Tag = tag,
            Status = AssetStatus.Active,
            IsRentable = false,
        };

    public static AssetWorkingHours Hours(Guid tenantId, Guid assetId, DayOfWeek day, int openHour, int closeHour) =>
        new()
        {
            TenantId = tenantId,
            AssetId = assetId,
            DayOfWeek = (short)day,
            OpenTime = new TimeOnly(openHour, 0),
            CloseTime = new TimeOnly(closeHour, 0),
        };
}

/// <summary>Module accessor that returns a fixed set of active modules.</summary>
internal sealed class FixedModuleAccessor(params string[] activeModules) : ITenantModuleAccessor
{
    public Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(activeModules, StringComparer.Ordinal));
}

/// <summary>A caller whose id and manage right the test sets.</summary>
internal sealed class FakePlanningCaller(Guid? userId, bool canManage) : IPlanningCallerAccessor
{
    public Guid? UserId { get; set; } = userId;

    public bool CanManage { get; set; } = canManage;

    public Task<PlanningCaller> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PlanningCaller(UserId, CanManage));
}
