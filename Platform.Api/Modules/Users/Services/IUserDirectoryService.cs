using System.Security.Claims;
using Platform.Api.Modules.Users.Dtos;

namespace Platform.Api.Modules.Users.Services;

public interface IUserDirectoryService
{
    Task<CurrentUserResponse> GetCurrentAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TechnicianUserResponse>> ListTechniciansAsync(
        CancellationToken cancellationToken);
}
