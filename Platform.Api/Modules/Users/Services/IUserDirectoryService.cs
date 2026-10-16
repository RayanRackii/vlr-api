using System.Security.Claims;
using Platform.Api.Authorization;
using Platform.Api.Common;
using Platform.Api.Modules.Users.Dtos;

namespace Platform.Api.Modules.Users.Services;

public interface IUserDirectoryService
{
    Task<CurrentUserResponse> GetCurrentAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<CurrentUserResponse> UpdateCurrentPhoneAsync(
        ClaimsPrincipal principal,
        string? phone,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TechnicianUserResponse>> ListTechniciansAsync(
        CancellationToken cancellationToken);

    Task<PagedResult<TenantMemberResponse>> ListAsync(
        DirectoryPage page,
        CancellationToken cancellationToken);

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
