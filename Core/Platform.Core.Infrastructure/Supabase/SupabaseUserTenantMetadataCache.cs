using System.Collections.Concurrent;

namespace Platform.Core.Infrastructure.Supabase;

/// <summary>
/// Short-lived per-auth-id copy of live <c>app_metadata.tenant_id</c>.
/// Enter and exit drop the entry so the next read hits Auth again.
/// </summary>
public sealed class SupabaseUserTenantMetadataCache
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, int> _epochs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal bool TryGet(string authId, out Guid? tenantId)
    {
        tenantId = null;
        var key = Normalize(authId);
        if (key.Length == 0 || !_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (!_epochs.TryGetValue(key, out var epoch) || epoch != entry.Epoch)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        tenantId = entry.TenantId;
        return true;
    }

    internal int CaptureEpoch(string authId)
    {
        var key = Normalize(authId);
        return _epochs.GetOrAdd(key, 0);
    }

    internal void Store(string authId, Guid? tenantId, int epoch)
    {
        var key = Normalize(authId);
        if (key.Length == 0)
        {
            return;
        }

        if (!_epochs.TryGetValue(key, out var current) || current != epoch)
        {
            return;
        }

        _entries[key] = new Entry(tenantId, epoch, DateTimeOffset.UtcNow.Add(Duration));

        if (!_epochs.TryGetValue(key, out current) || current != epoch)
        {
            if (_entries.TryGetValue(key, out var stored) && stored.Epoch == epoch)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    internal void Invalidate(string authId)
    {
        var key = Normalize(authId);
        if (key.Length == 0)
        {
            return;
        }

        _epochs.AddOrUpdate(
            key,
            1,
            static (_, current) => current == int.MaxValue ? 0 : current + 1);
        _entries.TryRemove(key, out _);
    }

    private static string Normalize(string authId)
    {
        var trimmed = authId.Trim();
        return Guid.TryParse(trimmed, out var parsed)
            ? parsed.ToString("D")
            : trimmed;
    }

    private readonly record struct Entry(Guid? TenantId, int Epoch, DateTimeOffset ExpiresAt);
}
