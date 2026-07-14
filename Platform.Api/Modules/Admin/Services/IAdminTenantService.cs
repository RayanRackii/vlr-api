using Platform.Api.Modules.Admin.Dtos;

namespace Platform.Api.Modules.Admin.Services;

public interface IAdminTenantService
{
    Task<IReadOnlyList<TenantAdminResponseDto>> ListAsync(CancellationToken cancellationToken);

    Task<TenantAdminResponseDto> CreateAsync(
        CreateTenantRequestDto request,
        CancellationToken cancellationToken);
}
