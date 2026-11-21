using Platform.Api.Services.Trial;

namespace Platform.Api.Tests.Fakes;

public sealed class FakeTrialGuard : ITrialGuard
{
    public Task EnsureWritableAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnsureCanCreateAssetsAsync(int additionalCount, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task EnsureCanInviteUserAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
