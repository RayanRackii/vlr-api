using Platform.Api.Modules.RegistrationFields.Dtos;

namespace Platform.Api.Modules.RegistrationFields.Services;

public interface IRegistrationFieldService
{
    Task<RegistrationSchemaResponseDto> GetSchemaBySubdomainAsync(
        string subdomain,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RegistrationFieldDto>> ListForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<RegistrationFieldDto> CreateAsync(
        Guid tenantId,
        UpsertRegistrationFieldRequestDto request,
        CancellationToken cancellationToken);

    Task<RegistrationFieldDto> UpdateAsync(
        Guid tenantId,
        Guid fieldId,
        UpdateRegistrationFieldRequestDto request,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid tenantId,
        Guid fieldId,
        CancellationToken cancellationToken);
}
