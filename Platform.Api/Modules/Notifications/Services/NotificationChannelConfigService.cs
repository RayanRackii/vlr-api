using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Notifications.Services;

public sealed class NotificationChannelConfigService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITenantModuleAccessor tenantModuleAccessor,
    ICatalogNotificationPublisher catalogPublisher,
    IRentalsNotificationPublisher rentalsPublisher) : INotificationChannelConfigService
{
    public async Task<IReadOnlyList<NotificationChannelConfigGroupResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        RequireTenant();
        var activeModules = await tenantModuleAccessor.GetActiveModuleKeysAsync(cancellationToken);
        await EnsureReadyForActiveModulesAsync(activeModules, cancellationToken);

        var configs = await dbContext.TenantNotificationChannelConfigs
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var lookup = configs.ToDictionary(
            item => (item.EventType, item.Channel),
            item => item.IsActive);

        var groups = new List<NotificationChannelConfigGroupResponse>();
        foreach (var moduleGroup in NotificationChannelCapability.GroupsInOrder)
        {
            if (!activeModules.Contains(moduleGroup.Key))
            {
                continue;
            }

            var events = moduleGroup
                .Select(eventCapability => new NotificationEventConfigResponse(
                    eventCapability.EventType,
                    eventCapability.DisplayKey,
                    eventCapability.Channels
                        .Select(channel => new NotificationChannelStateResponse(
                            channel.Channel,
                            lookup.TryGetValue((eventCapability.EventType, channel.Channel), out var isActive)
                                && isActive,
                            channel.Configurable))
                        .ToList()))
                .ToList();

            groups.Add(new NotificationChannelConfigGroupResponse(moduleGroup.Key, events));
        }

        return groups;
    }

    public async Task<UpsertNotificationChannelConfigResponse> UpsertAsync(
        UpsertNotificationChannelConfigRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var activeModules = await tenantModuleAccessor.GetActiveModuleKeysAsync(cancellationToken);
        await EnsureReadyForActiveModulesAsync(activeModules, cancellationToken);

        if (request.Channel == NotificationChannel.Sms)
        {
            throw new ArgumentException("SMS channel is not available.");
        }

        if (!NotificationChannelCapability.TryGetEvent(request.EventType, out var eventCapability))
        {
            throw new ArgumentException("Unknown notification event.");
        }

        if (!activeModules.Contains(eventCapability.ModuleKey))
        {
            throw new TenantModuleInactiveException();
        }

        if (!NotificationChannelCapability.TryGetChannel(request.EventType, request.Channel, out var channelCapability)
            || !channelCapability.Configurable)
        {
            throw new ArgumentException("Channel is not configurable for this event.");
        }

        var config = await dbContext.TenantNotificationChannelConfigs
            .FirstOrDefaultAsync(
                item => item.EventType == request.EventType && item.Channel == request.Channel,
                cancellationToken);

        if (config is null)
        {
            config = new TenantNotificationChannelConfig
            {
                TenantId = tenantId,
                EventType = request.EventType,
                Channel = request.Channel,
                IsActive = request.IsActive,
            };
            dbContext.TenantNotificationChannelConfigs.Add(config);
        }
        else
        {
            config.IsActive = request.IsActive;
            config.Touch();
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return new UpsertNotificationChannelConfigResponse(config.EventType, config.Channel, config.IsActive);
    }

    private async Task EnsureReadyForActiveModulesAsync(
        IReadOnlySet<string> activeModules,
        CancellationToken cancellationToken)
    {
        if (activeModules.Contains(PlatformModules.Catalog))
        {
            await catalogPublisher.EnsureReadyAsync(cancellationToken);
        }

        if (activeModules.Contains(PlatformModules.Rentals))
        {
            await rentalsPublisher.EnsureReadyAsync(cancellationToken);
        }
    }

    private Guid RequireTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
