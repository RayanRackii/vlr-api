using Platform.Api.Modules.Admin.Dtos;

namespace Platform.Api.Modules.Admin.Services;

public interface IAdminTenantService
{
    Task<IReadOnlyList<TenantAdminResponseDto>> ListAsync(CancellationToken cancellationToken);

    Task<TenantAdminResponseDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<TenantAdminResponseDto> CreateAsync(
        CreateTenantRequestDto request,
        CancellationToken cancellationToken);

    Task<TenantAdminResponseDto> UpdateAsync(
        Guid id,
        UpdateTenantRequestDto request,
        CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
