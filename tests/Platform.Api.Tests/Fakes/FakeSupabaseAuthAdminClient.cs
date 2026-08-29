using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Fakes;

public sealed class FakeSupabaseAuthAdminClient : ISupabaseAuthAdminClient
{
    public string NextUserId { get; set; } = Guid.NewGuid().ToString("N");

    public Task<string> CreateUserAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextUserId);

    public Task<string?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<bool> UserExistsAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task UpdateUserAppMetadataAsync(
        string supabaseUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ClearUserTenantAppMetadataAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetUserPasswordAsync(
        string supabaseUserId,
        string password,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeleteUserAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
        string email,
        string redirectTo,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SupabaseRecoveryLink("token", null));
}
