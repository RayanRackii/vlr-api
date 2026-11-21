using Platform.Api.Services.Billing;

namespace Platform.Api.Tests.Fakes;

public sealed class FakePlanGuard : IPlanGuard
{
    public Task EnsureWritableAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnsureCanCreateAssetsAsync(
        int additionalCount,
        int additionalRentableCount,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnsureCanInviteUserAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnsureCanCreateUnitAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
