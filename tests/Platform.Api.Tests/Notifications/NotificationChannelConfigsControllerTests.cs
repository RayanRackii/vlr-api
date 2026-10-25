using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Notifications;
using Platform.Api.Modules.Notifications.Controllers;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Notifications;

public sealed class NotificationChannelConfigsControllerTests
{
    [Fact]
    public async Task Upsert_module_inactive_returns_stable_error_code_and_module()
    {
        var controller = new NotificationChannelConfigsController(
            new InactiveModuleService(PlatformModules.Rentals));

        var result = await controller.Upsert(
            new UpsertNotificationChannelConfigRequest
            {
                EventType = "reservation.confirmed",
                Channel = NotificationChannel.Email,
                IsActive = true,
            },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(objectResult.Value));
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleError, body.RootElement.GetProperty("error").GetString());
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleCode, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(PlatformModules.Rentals, body.RootElement.GetProperty("module").GetString());
    }

    private sealed class InactiveModuleService(string moduleKey) : INotificationChannelConfigService
    {
        public Task<IReadOnlyList<NotificationChannelConfigGroupResponse>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UpsertNotificationChannelConfigResponse> UpsertAsync(
            UpsertNotificationChannelConfigRequest request,
            CancellationToken cancellationToken) =>
            throw new TenantModuleInactiveException(moduleKey);
    }
}
