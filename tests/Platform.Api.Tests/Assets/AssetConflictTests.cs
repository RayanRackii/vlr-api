using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Platform.Api.Jobs;
using Platform.Api.Modules.Assets;
using Platform.Api.Modules.Assets.Controllers;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Assets;

public sealed class AssetConflictTests
{
    [Fact]
    public async Task Create_duplicate_tag_returns_409_ASSET_TAG_DUPLICATE()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();
        var controller = new AssetsController(service);
        await service.CreateAsync(AssetRequest(harness, "Q1"), CancellationToken.None);

        var result = await controller.Create(AssetRequest(harness, "Q1"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal(AssetTagDuplicateException.Code, doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, await harness.Db.Assets.CountAsync());
    }

    [Fact]
    public async Task Bulk_create_duplicate_tag_is_ASSET_TAG_DUPLICATE()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();
        await service.CreateAsync(AssetRequest(harness, "Q-1"), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<AssetTagDuplicateException>(() =>
            service.BulkCreateAsync(
                new BulkCreateAssetsRequest
                {
                    UnitId = harness.UnitId,
                    CategoryId = harness.CategoryId,
                    FamilyId = harness.FamilyId,
                    BaseLocationName = "Site",
                    BaseTag = "Q-",
                    StartNumber = 1,
                    EndNumber = 2,
                    RentalType = RentalAssetType.Location,
                    IsRentable = true,
                },
                CancellationToken.None));

        Assert.Contains("Q-1", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await harness.Db.Assets.CountAsync());
    }

    [Fact]
    public async Task Permanent_delete_of_asset_with_work_order_returns_ASSET_IN_USE()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();
        var created = await service.CreateAsync(AssetRequest(harness, "Q1"), CancellationToken.None);
        await service.DeleteAsync(created.Id, CancellationToken.None);
        harness.Db.WorkOrders.Add(new WorkOrder
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            AssetId = created.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = new DateOnly(2026, 10, 1),
        });
        await harness.Db.SaveChangesAsync();
        var controller = new AssetsController(service);

        var result = await controller.Delete(created.Id, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("ASSET_IN_USE", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, await harness.Db.Assets.CountAsync(asset => asset.Id == created.Id));
    }

    [Fact]
    public async Task Permanent_delete_of_category_used_by_plan_or_work_order_returns_CATEGORY_IN_USE()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var categories = new AssetCategoryService(harness.Db, harness.TenantProvider);
        var controller = new AssetCategoriesController(categories);
        var planned = await categories.CreateAsync(
            new CreateAssetCategoryRequest { Name = "With plan" },
            CancellationToken.None);
        var used = await categories.CreateAsync(
            new CreateAssetCategoryRequest { Name = "With orders" },
            CancellationToken.None);
        await categories.DeleteAsync(planned.Id, CancellationToken.None);
        await categories.DeleteAsync(used.Id, CancellationToken.None);

        harness.Db.MaintenancePlans.Add(new MaintenancePlan
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            Name = "Monthly",
            IntervalDays = 30,
            FirstDueDate = new DateOnly(2026, 10, 1),
            AssetCategoryId = planned.Id,
            IsActive = true,
        });
        var asset = await harness.CreateService().CreateAsync(
            AssetRequest(harness, "OS-1") with { CategoryId = used.Id },
            CancellationToken.None);
        harness.Db.WorkOrders.Add(new WorkOrder
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = new DateOnly(2026, 10, 2),
        });
        await harness.Db.SaveChangesAsync();

        var planConflict = Assert.IsType<ConflictObjectResult>(
            (await controller.Delete(planned.Id, CancellationToken.None)).Result);
        var workOrderConflict = Assert.IsType<ConflictObjectResult>(
            (await controller.Delete(used.Id, CancellationToken.None)).Result);

        using var planDoc = JsonDocument.Parse(JsonSerializer.Serialize(planConflict.Value));
        using var workOrderDoc = JsonDocument.Parse(JsonSerializer.Serialize(workOrderConflict.Value));
        Assert.Equal("CATEGORY_IN_USE", planDoc.RootElement.GetProperty("code").GetString());
        Assert.Contains("maintenance plans", planDoc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("CATEGORY_IN_USE", workOrderDoc.RootElement.GetProperty("code").GetString());
        Assert.Contains("work orders", workOrderDoc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, await harness.Db.AssetCategories.CountAsync(category =>
            category.Id == planned.Id || category.Id == used.Id));
    }

    [Fact]
    public async Task Data_retention_skips_referenced_rows_and_deletes_clean_ones()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var tenantId = harness.TenantProvider.TenantId!.Value;
        var expired = DateTimeOffset.UtcNow.AddDays(-1);
        var blockedCategory = new AssetCategory
        {
            TenantId = tenantId,
            Name = "Blocked",
            ScheduledDeletionAt = expired,
        };
        var cleanCategory = new AssetCategory
        {
            TenantId = tenantId,
            Name = "Clean",
            ScheduledDeletionAt = expired,
        };
        harness.Db.AssetCategories.AddRange(blockedCategory, cleanCategory);
        await harness.Db.SaveChangesAsync();

        var blockedAsset = new Asset
        {
            TenantId = tenantId,
            UnitId = harness.UnitId,
            CategoryId = blockedCategory.Id,
            FamilyId = harness.FamilyId,
            Name = "Linked",
            Tag = "RET-1",
            Status = AssetStatus.Active,
            ScheduledDeletionAt = expired,
        };
        var cleanAsset = new Asset
        {
            TenantId = tenantId,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = "Free",
            Tag = "RET-2",
            Status = AssetStatus.Active,
            ScheduledDeletionAt = expired,
        };
        harness.Db.Assets.AddRange(blockedAsset, cleanAsset);
        harness.Db.MaintenancePlans.Add(new MaintenancePlan
        {
            TenantId = tenantId,
            UnitId = harness.UnitId,
            Name = "Plan",
            IntervalDays = 30,
            FirstDueDate = new DateOnly(2026, 10, 1),
            AssetCategoryId = blockedCategory.Id,
            IsActive = true,
        });
        await harness.Db.SaveChangesAsync();
        harness.Db.WorkOrders.Add(new WorkOrder
        {
            TenantId = tenantId,
            AssetId = blockedAsset.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = new DateOnly(2026, 10, 3),
        });
        await harness.Db.SaveChangesAsync();

        var job = new DataRetentionJob(harness.Db, NullLogger<DataRetentionJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None);

        Assert.True(await harness.Db.AssetCategories.AnyAsync(category => category.Id == blockedCategory.Id));
        Assert.False(await harness.Db.AssetCategories.AnyAsync(category => category.Id == cleanCategory.Id));
        Assert.True(await harness.Db.Assets.AnyAsync(asset => asset.Id == blockedAsset.Id));
        Assert.False(await harness.Db.Assets.AnyAsync(asset => asset.Id == cleanAsset.Id));
    }

    [Fact]
    public void Tag_unique_and_work_order_foreign_key_matchers_are_specific()
    {
        var tag = new DbUpdateException(
            "conflict",
            new PostgresException(
                "duplicate key value violates unique constraint \"ix_assets_tenant_id_tag\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));
        var otherUnique = new DbUpdateException(
            "conflict",
            new PostgresException(
                "duplicate key value violates unique constraint \"ux_users_email\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));
        var workOrderFk = new DbUpdateException(
            "conflict",
            new PostgresException(
                "violates foreign key constraint \"fk_work_orders_assets_asset_id\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.ForeignKeyViolation));

        Assert.True(AssetPersistenceConflicts.IsTagUniqueViolation(tag));
        Assert.False(AssetPersistenceConflicts.IsTagUniqueViolation(otherUnique));
        Assert.True(AssetPersistenceConflicts.IsWorkOrderAssetForeignKey(workOrderFk));
        Assert.False(AssetPersistenceConflicts.IsWorkOrderAssetForeignKey(tag));
    }

    private static CreateAssetRequest AssetRequest(BulkCreateAssetsHarness harness, string tag) =>
        new()
        {
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = tag,
            Tag = tag,
            Status = AssetStatus.Active,
        };
}
