namespace Platform.Core.Infrastructure.Supabase;

public sealed record SupabaseRecoveryLink(
    string HashedToken,
    string? ActionLink);

/// <summary>
/// The <c>tenant_id</c> carried by the request's access token (null when it has none).
/// A cached live value may be served only when it equals this value. The token is never
/// a source of the tenant.
/// </summary>
public readonly record struct TenantMetadataCacheHint(Guid? TokenTenantId);

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

    /// <summary>
    /// Live <c>app_metadata.tenant_id</c> for the Auth user, or null when it is absent.
    /// This is the server-side support session. Callers must not substitute the access token.
    /// </summary>
    Task<Guid?> GetUserTenantAppMetadataAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same live read, but <paramref name="hint"/> decides whether a cached value may be used.
    /// The hint is only a reason to skip the cache; the returned tenant always comes from
    /// Auth admin (directly or from a cache entry that agrees with the hint).
    /// The default implementation ignores the hint, so fakes need not implement it.
    /// </summary>
    Task<Guid?> GetUserTenantAppMetadataAsync(
        string supabaseUserId,
        TenantMetadataCacheHint hint,
        CancellationToken cancellationToken = default) =>
        GetUserTenantAppMetadataAsync(supabaseUserId, cancellationToken);

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
