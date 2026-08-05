using Platform.Api.Modules.Admin.Dtos;
using System.Security.Claims;

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

    Task<EnterTenantEnvironmentResponseDto> EnterEnvironmentAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task ExitEnvironmentAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);
}
