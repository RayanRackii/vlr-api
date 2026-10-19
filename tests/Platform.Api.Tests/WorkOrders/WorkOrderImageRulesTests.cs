using Platform.Api.Modules.WorkOrders;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderImageRulesTests
{
    [Fact]
    public void Jpeg_png_and_webp_match_content_type_and_magic_bytes()
    {
        Assert.Equal(
            "image/jpeg",
            WorkOrderImageRules.Validate("image/jpeg; charset=binary", 4, [0xFF, 0xD8, 0xFF, 0x00]));
        Assert.Equal(
            "image/png",
            WorkOrderImageRules.Validate(
                "image/png",
                8,
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));

        var webp = new byte[12];
        webp[0] = (byte)'R';
        webp[1] = (byte)'I';
        webp[2] = (byte)'F';
        webp[3] = (byte)'F';
        webp[8] = (byte)'W';
        webp[9] = (byte)'E';
        webp[10] = (byte)'B';
        webp[11] = (byte)'P';
        Assert.Equal("image/webp", WorkOrderImageRules.Validate("IMAGE/WEBP", 12, webp));
    }

    [Fact]
    public void Storage_key_is_scoped_to_the_tenant_work_order_and_task()
    {
        var tenantId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var otherTenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var workOrderId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var taskId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var objectId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var key = WorkOrderImageRules.StorageKey(tenantId, workOrderId, taskId, objectId);

        Assert.StartsWith($"{tenantId:N}/", key, StringComparison.Ordinal);
        Assert.True(WorkOrderImageRules.IsKeyForTask(key, tenantId, workOrderId, taskId));
        Assert.False(WorkOrderImageRules.IsKeyForTask(key, otherTenantId, workOrderId, taskId));
        Assert.False(WorkOrderImageRules.IsKeyForTask($"{tenantId:N}/products/other", tenantId, workOrderId, taskId));
    }
}
