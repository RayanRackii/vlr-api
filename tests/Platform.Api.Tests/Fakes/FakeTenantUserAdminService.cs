using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;

namespace Platform.Api.Tests.Fakes;

public sealed class FakeTenantUserAdminService : ITenantUserAdminService
{
    public Task<TenantUsersBundleDto> ListUsersAndInvitesAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<TenantInviteResponseDto> InviteAsync(
        Guid tenantId,
        InviteTenantUserRequestDto request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task ResendInviteAsync(Guid tenantId, Guid inviteId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task RevokeInviteAsync(Guid tenantId, Guid inviteId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task PromoteAsync(
        Guid tenantId,
        Guid userId,
        PromoteTenantUserRequestDto request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AcceptInviteResponseDto> AcceptInviteAsync(
        AcceptInviteRequestDto request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
