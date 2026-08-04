using Platform.Api.Modules.ModuleMenuItems.Dtos;

namespace Platform.Api.Modules.ModuleMenuItems.Services;

public interface IModuleMenuItemService
{
    Task<IReadOnlyList<ModuleMenuItemDto>> GetPublicMenuBySubdomainAsync(
        string subdomain,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ModuleMenuItemDto>> ListForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<ModuleMenuItemDto> CreateAsync(
        Guid tenantId,
        UpsertModuleMenuItemRequestDto request,
        CancellationToken cancellationToken);

    Task<ModuleMenuItemDto> UpdateAsync(
        Guid tenantId,
        Guid itemId,
        UpdateModuleMenuItemRequestDto request,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid tenantId,
        Guid itemId,
        CancellationToken cancellationToken);
}
