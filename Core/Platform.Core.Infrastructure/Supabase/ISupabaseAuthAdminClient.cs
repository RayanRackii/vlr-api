namespace Platform.Core.Infrastructure.Supabase;

public sealed record SupabaseRecoveryLink(
    string HashedToken,
    string? ActionLink);

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

    Task<bool> UserExistsAsync(
        string supabaseUserId,
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

    /// <summary>
    /// Generates a recovery token without sending Supabase's email.
    /// Prefer <see cref="SupabaseRecoveryLink.HashedToken"/> in a first-party URL
    /// (<c>/reset-password?token_hash=…&amp;type=recovery</c>) — do not rely on
    /// <c>action_link</c> redirects (Site URL fallback often points to localhost).
    /// </summary>
    Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
        string email,
        string redirectTo,
        CancellationToken cancellationToken = default);
}
