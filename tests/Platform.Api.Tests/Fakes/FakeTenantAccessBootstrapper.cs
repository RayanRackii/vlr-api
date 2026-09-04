using System.Security.Claims;
using Platform.Api.Authorization;
using Platform.Api.Modules.Admin.Services;

namespace Platform.Api.Tests.Fakes;

public sealed class FakeTenantAccessBootstrapper : ITenantAccessBootstrapper
{
    public Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class FakePlatformAdminMembershipService : IPlatformAdminMembershipService
{
    public Task ProvisionPlatformAdminsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnsureMembershipAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnterTenantAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ExitTenantAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
