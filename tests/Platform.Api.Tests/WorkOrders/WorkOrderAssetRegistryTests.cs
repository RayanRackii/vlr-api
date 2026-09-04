using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderAssetRegistryTests
{
    [Fact]
    public async Task Create_with_foreign_tenant_asset_throws_KeyNotFound()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var foreignTenant = new Tenant("OS Other", "99999999000191", subdomain: "os-other");
        var foreignUnit = new Unit(foreignTenant.Id, "Outra");
        var foreignCategory = new AssetCategory { TenantId = foreignTenant.Id, Name = "Outras" };
        harness.Db.Tenants.Add(foreignTenant);
        harness.Db.Units.Add(foreignUnit);
        harness.Db.AssetCategories.Add(foreignCategory);
        await harness.Db.SaveChangesAsync();

        var foreignAsset = new Asset
        {
            TenantId = foreignTenant.Id,
            UnitId = foreignUnit.Id,
            CategoryId = foreignCategory.Id,
            FamilyId = harness.FamilyId,
            Name = "Foreign",
            Tag = "F1",
            Status = AssetStatus.Active,
        };
        harness.Db.Assets.Add(foreignAsset);
        await harness.Db.SaveChangesAsync();

        Assert.True(
            await harness.Db.Assets.IgnoreQueryFilters()
                .AnyAsync(a => a.Id == foreignAsset.Id));

        var service = CreateWorkOrderService(harness);

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.CreateAsync(
                new CreateWorkOrderRequest
                {
                    AssetId = foreignAsset.Id,
                    ScheduledDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Tasks =
                    [
                        new CreateWorkOrderTaskDto
                        {
                            Title = "Inspecionar",
                            InputType = TaskInputType.Checkbox,
                            Order = 1,
                        },
                    ],
                },
                CancellationToken.None));

        Assert.Contains(foreignAsset.Id.ToString(), ex.Message, StringComparison.Ordinal);
    }

    private static WorkOrderService CreateWorkOrderService(BulkCreateAssetsHarness harness)
    {
        var http = new FakeHttpContextAccessor();
        var resolver = new PermissionResolver(harness.Db, NullLogger<PermissionResolver>.Instance);
        var grantGuard = new RbacGrantGuard(harness.Db, resolver, NullLogger<RbacGrantGuard>.Instance);
        var users = new UserDirectoryService(
            harness.Db,
            harness.TenantProvider,
            new FakePlatformAdminChecker(),
            resolver,
            grantGuard,
            new FakeTrialGuard(),
            new NotificationQueue(),
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(),
            NullLogger<UserDirectoryService>.Instance);

        return new WorkOrderService(
            harness.Db,
            harness.TenantProvider,
            http,
            users,
            resolver,
            harness.CreateRegistry());
    }
}
