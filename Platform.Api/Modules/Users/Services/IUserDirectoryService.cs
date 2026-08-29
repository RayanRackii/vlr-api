using System.Security.Claims;
using Platform.Api.Authorization;
using Platform.Api.Modules.Users.Dtos;

namespace Platform.Api.Modules.Users.Services;

public interface IUserDirectoryService
{
    Task<CurrentUserResponse> GetCurrentAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TechnicianUserResponse>> ListTechniciansAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantMemberResponse>> ListAsync(CancellationToken cancellationToken);

    Task AssignRolesAsync(
        Guid userId,
        RbacActor actor,
        IReadOnlyList<Guid> roleIds,
        CancellationToken cancellationToken);

    Task<InviteTenantMemberResponse> InviteAsync(
        RbacActor actor,
        InviteTenantMemberRequest request,
        CancellationToken cancellationToken);
}
