using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Api.Tests.Assets;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Pmoc;

public sealed class MaintenancePlanAssetRegistryTests
{
    [Fact]
    public async Task Create_with_foreign_tenant_category_throws_KeyNotFound()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var foreignTenant = new Tenant("PMOC Other", "10101010000191", subdomain: "pmoc-other");
        var foreignCategory = new AssetCategory { TenantId = foreignTenant.Id, Name = "Foreign cat" };
        harness.Db.Tenants.Add(foreignTenant);
        harness.Db.AssetCategories.Add(foreignCategory);
        await harness.Db.SaveChangesAsync();

        Assert.True(
            await harness.Db.AssetCategories.IgnoreQueryFilters()
                .AnyAsync(c => c.Id == foreignCategory.Id));

        var service = new MaintenancePlanService(
            harness.Db,
            harness.TenantProvider,
            harness.CreateRegistry());

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CreatePlanWithTasksAsync(
                new CreateMaintenancePlanRequest
                {
                    UnitId = harness.UnitId,
                    Name = "Plano",
                    Frequency = MaintenanceFrequency.Monthly,
                    AssetCategoryId = foreignCategory.Id,
                    Tasks =
                    [
                        new CreatePlanTaskDto
                        {
                            Title = "Filtro",
                            InputType = TaskInputType.Checkbox,
                            Order = 1,
                        },
                    ],
                },
                CancellationToken.None));

        Assert.Contains(foreignCategory.Id.ToString(), ex.Message, StringComparison.Ordinal);
    }
}
