using System.Collections.Concurrent;

namespace Platform.Core.Infrastructure.Supabase;

/// <summary>
/// In-process, per-auth-id copy of live <c>app_metadata.tenant_id</c>.
/// Assumes a single API replica: the dictionary is not shared, so another
/// process can still serve the previous tenant until this TTL after exit.
/// Enter and exit drop the entry on this process so the next read hits Auth again.
/// Epoch keys are dropped once they are older than <see cref="Duration"/>. The
/// sweep runs at most once a second, and the map stops tracking new auth ids at
/// <see cref="DefaultMaxTrackedAuthIds"/>. An epoch value may be reused after that
/// TTL. Auth admin calls are bounded by <see cref="SupabaseAuthAdminClient.RequestTimeout"/>,
/// which is shorter, so a read still in flight cannot store into a reused epoch.
/// </summary>
public sealed class SupabaseUserTenantMetadataCache
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    internal const int DefaultMaxTrackedAuthIds = 8192;

    private const int UntrackedEpoch = int.MinValue;

    private readonly int _maxTrackedAuthIds;
    private readonly ConcurrentDictionary<string, EpochState> _epochs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _nextEpochSweepTicks;

    public SupabaseUserTenantMetadataCache()
        : this(DefaultMaxTrackedAuthIds)
    {
    }

    internal SupabaseUserTenantMetadataCache(int maxTrackedAuthIds)
    {
        if (maxTrackedAuthIds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTrackedAuthIds));
        }

        _maxTrackedAuthIds = maxTrackedAuthIds;
    }

    internal int TrackedAuthIdCount => _epochs.Count;

    internal bool TryGet(string authId, out Guid? tenantId)
    {
        tenantId = null;
        var key = Normalize(authId);
        if (key.Length == 0)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        SweepExpired(now);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (!_epochs.TryGetValue(key, out var epoch) || epoch.Value != entry.Epoch)
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        if (entry.ExpiresAt <= now)
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
        var now = DateTimeOffset.UtcNow;
        SweepExpired(now);
        if (key.Length == 0)
        {
            return UntrackedEpoch;
        }

        if (_epochs.TryGetValue(key, out var existing))
        {
            _epochs.TryUpdate(key, new EpochState(existing.Value, now), existing);
            return existing.Value;
        }

        if (_epochs.Count >= _maxTrackedAuthIds)
        {
            return UntrackedEpoch;
        }

        var added = new EpochState(0, now);
        if (!_epochs.TryAdd(key, added))
        {
            return _epochs.TryGetValue(key, out var raced) ? raced.Value : UntrackedEpoch;
        }

        return added.Value;
    }

    internal void Store(string authId, Guid? tenantId, int epoch)
    {
        var key = Normalize(authId);
        if (key.Length == 0 || epoch == UntrackedEpoch)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        SweepExpired(now);
        if (!_epochs.TryGetValue(key, out var current) || current.Value != epoch)
        {
            return;
        }

        _entries[key] = new Entry(tenantId, epoch, now.Add(Duration));

        if (!_epochs.TryGetValue(key, out current) || current.Value != epoch)
        {
            if (_entries.TryGetValue(key, out var stored) && stored.Epoch == epoch)
            {
                _entries.TryRemove(key, out _);
            }
        }
        else
        {
            _epochs.TryUpdate(key, new EpochState(epoch, now), current);
        }
    }

    internal void Invalidate(string authId)
    {
        var key = Normalize(authId);
        if (key.Length == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        SweepExpired(now);
        while (true)
        {
            if (_epochs.TryGetValue(key, out var existing))
            {
                var next = existing.Value == int.MaxValue ? 0 : existing.Value + 1;
                if (_epochs.TryUpdate(key, new EpochState(next, now), existing))
                {
                    break;
                }

                continue;
            }

            if (_epochs.Count >= _maxTrackedAuthIds
                || _epochs.TryAdd(key, new EpochState(1, now)))
            {
                break;
            }
        }

        _entries.TryRemove(key, out _);
    }

    internal void EvictExpired(DateTimeOffset now)
    {
        var cutoff = now - Duration;
        foreach (var pair in _epochs)
        {
            if (pair.Value.TouchedAt <= cutoff)
            {
                _epochs.TryRemove(pair);
            }
        }

        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now
                || !_epochs.TryGetValue(pair.Key, out var epoch)
                || epoch.Value != pair.Value.Epoch)
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private void SweepExpired(DateTimeOffset now)
    {
        var due = now.UtcTicks;
        var next = Volatile.Read(ref _nextEpochSweepTicks);
        if (next > due)
        {
            return;
        }

        var scheduled = due + TimeSpan.TicksPerSecond;
        if (Interlocked.CompareExchange(ref _nextEpochSweepTicks, scheduled, next) != next)
        {
            return;
        }

        EvictExpired(now);
    }

    private static string Normalize(string authId)
    {
        var trimmed = authId.Trim();
        return Guid.TryParse(trimmed, out var parsed)
            ? parsed.ToString("D")
            : trimmed;
    }

    private readonly record struct EpochState(int Value, DateTimeOffset TouchedAt);

    private readonly record struct Entry(Guid? TenantId, int Epoch, DateTimeOffset ExpiresAt);
}
