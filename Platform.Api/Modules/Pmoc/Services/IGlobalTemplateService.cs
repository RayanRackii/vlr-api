using Platform.Api.Modules.Pmoc.Dtos;

namespace Platform.Api.Modules.Pmoc.Services;

public interface IGlobalTemplateService
{
    Task<IReadOnlyList<GlobalMaintenanceTemplateResponse>> ListAsync(
        string? jurisdiction,
        CancellationToken cancellationToken);

    Task<GlobalMaintenanceTemplateResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken);
}
