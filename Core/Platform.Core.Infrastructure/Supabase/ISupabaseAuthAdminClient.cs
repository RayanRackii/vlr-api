namespace Platform.Core.Infrastructure.Supabase;

public interface ISupabaseAuthAdminClient
{
    Task<string> CreateUserAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the Auth user id for <paramref name="email"/>, or null if not found.
    /// </summary>
    Task<string?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task UpdateUserAppMetadataAsync(
        string supabaseUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears <c>app_metadata.tenant_id</c> so Platform Super-Admins return to cross-tenant mode.
    /// </summary>
    Task ClearUserTenantAppMetadataAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default);

    Task SetUserPasswordAsync(
        string supabaseUserId,
        string password,
        CancellationToken cancellationToken = default);

    Task DeleteUserAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default);
}
