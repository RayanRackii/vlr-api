using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Fakes;

public sealed class FakeSupabaseAuthAdminClient : ISupabaseAuthAdminClient
{
    private readonly Dictionary<string, Guid?> _tenantByUser = new(StringComparer.Ordinal);

    public string NextUserId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>When set, email lookup returns this Auth id instead of null.</summary>
    public string? EmailLookupUserId { get; set; }

    public Task<string> CreateUserAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextUserId);

    public Task<string?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(EmailLookupUserId);

    public Task<bool> UserExistsAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task UpdateUserAppMetadataAsync(
        string supabaseUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        _tenantByUser[supabaseUserId] = tenantId;
        return Task.CompletedTask;
    }

    public Task ClearUserTenantAppMetadataAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default)
    {
        _tenantByUser[supabaseUserId] = null;
        return Task.CompletedTask;
    }

    public Task<Guid?> GetUserTenantAppMetadataAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            _tenantByUser.TryGetValue(supabaseUserId, out var tenantId) ? tenantId : null);

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
