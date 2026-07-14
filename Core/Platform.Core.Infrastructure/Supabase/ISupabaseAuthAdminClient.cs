namespace Platform.Core.Infrastructure.Supabase;

public interface ISupabaseAuthAdminClient
{
    Task<string> CreateUserAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default);

    Task UpdateUserAppMetadataAsync(
        string supabaseUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task DeleteUserAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default);
}
