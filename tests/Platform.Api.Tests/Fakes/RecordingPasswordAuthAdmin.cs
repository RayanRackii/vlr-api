using System.Security.Cryptography;
using System.Text;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Tests.Fakes;

/// <summary>
/// Auth admin double that stores a password hash per auth id.
/// <see cref="SetUserPasswordAsync"/> is the only way to replace a seeded hash.
/// </summary>
public sealed class RecordingPasswordAuthAdmin : ISupabaseAuthAdminClient
{
    private readonly Dictionary<string, string> _authIdByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hashByAuthId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid?> _tenantByAuthId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Email, string Password, string UserId)> _created = [];
    private readonly List<(string UserId, string Password)> _passwordsSet = [];
    private readonly List<(string UserId, Guid TenantId)> _metadataUpdates = [];
    private readonly List<string> _deleted = [];

    public string NextUserId { get; set; } = Guid.NewGuid().ToString();

    public Func<CancellationToken, Task>? AfterMetadataUpdate { get; set; }

    public IReadOnlyList<string> Deleted => _deleted;

    public IReadOnlyList<(string Email, string Password, string UserId)> Created => _created;

    public IReadOnlyList<(string UserId, string Password)> PasswordsSet => _passwordsSet;

    public IReadOnlyList<(string UserId, Guid TenantId)> MetadataUpdates => _metadataUpdates;

    public static string Hash(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

    public void RememberCredential(string authId, string password) =>
        _hashByAuthId[authId] = Hash(password);

    public void RegisterEmail(string email, string authId, Guid? tenantId = null)
    {
        _authIdByEmail[email.Trim().ToLowerInvariant()] = authId;
        _tenantByAuthId[authId] = tenantId;
    }

    public string HashOf(string authId) => _hashByAuthId[authId];

    public Guid? TenantOf(string authId) =>
        _tenantByAuthId.TryGetValue(authId, out var tenantId) ? tenantId : null;

    public Task<string> CreateUserAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        var key = email.Trim().ToLowerInvariant();
        if (_authIdByEmail.ContainsKey(key))
        {
            throw new InvalidOperationException("Auth user already exists.");
        }

        var userId = NextUserId;
        _authIdByEmail[key] = userId;
        _hashByAuthId[userId] = Hash(password);
        _created.Add((key, password, userId));
        return Task.FromResult(userId);
    }

    public Task<string?> FindUserIdByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var key = email.Trim().ToLowerInvariant();
        return Task.FromResult(_authIdByEmail.TryGetValue(key, out var authId) ? authId : null);
    }

    public Task<bool> UserExistsAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_hashByAuthId.ContainsKey(supabaseUserId));

    public async Task UpdateUserAppMetadataAsync(
        string supabaseUserId,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        _tenantByAuthId[supabaseUserId] = tenantId;
        _metadataUpdates.Add((supabaseUserId, tenantId));
        if (AfterMetadataUpdate is not null)
        {
            await AfterMetadataUpdate(cancellationToken);
        }
    }

    public Task ClearUserTenantAppMetadataAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default)
    {
        _tenantByAuthId[supabaseUserId] = null;
        return Task.CompletedTask;
    }

    public Task<Guid?> GetUserTenantAppMetadataAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(TenantOf(supabaseUserId));

    public Task SetUserPasswordAsync(
        string supabaseUserId,
        string password,
        CancellationToken cancellationToken = default)
    {
        _passwordsSet.Add((supabaseUserId, password));
        _hashByAuthId[supabaseUserId] = Hash(password);
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(
        string supabaseUserId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _deleted.Add(supabaseUserId);
        _hashByAuthId.Remove(supabaseUserId);
        _tenantByAuthId.Remove(supabaseUserId);
        foreach (var email in _authIdByEmail.Where(item => item.Value == supabaseUserId).Select(item => item.Key).ToList())
        {
            _authIdByEmail.Remove(email);
        }
        return Task.CompletedTask;
    }

    public Task<SupabaseRecoveryLink> GenerateRecoveryLinkAsync(
        string email,
        string redirectTo,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SupabaseRecoveryLink("token", null));
}
