namespace Platform.Api.Authentication;

/// <summary>
/// Shares one GoTrue probe per session id and remembers the outcome for a few seconds.
/// The key is the session id the caller already took from the validated access token.
/// This type does not accept or store the raw token.
/// </summary>
internal sealed class GoTrueSessionProbeCoordinator
{
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(2);
    internal const int DefaultCacheCapacity = 1024;
    internal const int DefaultInFlightCapacity = 1024;

    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly int _cacheCapacity;
    private readonly int _inFlightCapacity;
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<GoTrueAccessSessionStatus>> _inFlight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _sequence;

    public GoTrueSessionProbeCoordinator(TimeProvider timeProvider)
        : this(timeProvider, DefaultTtl, DefaultCacheCapacity, DefaultInFlightCapacity)
    {
    }

    internal GoTrueSessionProbeCoordinator(
        TimeProvider timeProvider,
        TimeSpan ttl,
        int cacheCapacity,
        int inFlightCapacity)
    {
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _ttl = ttl;
        _cacheCapacity = cacheCapacity;
        _inFlightCapacity = inFlightCapacity;
    }

    public async Task<GoTrueAccessSessionStatus> ExecuteAsync(
        string sessionId,
        Func<CancellationToken, Task<GoTrueAccessSessionStatus>> probe,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(probe);

        if (TryRead(sessionId, out var cached))
        {
            return cached;
        }

        var flight = Join(sessionId);
        if (flight.Created is { } created)
        {
            _ = DriveAsync(sessionId, created, probe);
            return await created.Task.WaitAsync(cancellationToken);
        }

        if (flight.Shared is { } shared)
        {
            return await shared.WaitAsync(cancellationToken);
        }

        var status = await probe(cancellationToken);
        Remember(sessionId, status);
        return status;
    }

    private Flight Join(string sessionId)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(sessionId, out var existing))
            {
                return new Flight(existing, null);
            }

            if (_inFlight.Count >= _inFlightCapacity)
            {
                return default;
            }

            var created = new TaskCompletionSource<GoTrueAccessSessionStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight.Add(sessionId, created.Task);
            return new Flight(created.Task, created);
        }
    }

    private async Task DriveAsync(
        string sessionId,
        TaskCompletionSource<GoTrueAccessSessionStatus> created,
        Func<CancellationToken, Task<GoTrueAccessSessionStatus>> probe)
    {
        try
        {
            if (TryRead(sessionId, out var cached))
            {
                created.TrySetResult(cached);
                return;
            }

            // None: one caller's disconnect must not cancel the shared probe.
            // The HttpClient timeout still bounds the call.
            var status = await probe(CancellationToken.None);
            Remember(sessionId, status);
            created.TrySetResult(status);
        }
        catch (Exception exception)
        {
            created.TrySetException(exception);
        }
        finally
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(sessionId, out var current)
                    && ReferenceEquals(current, created.Task))
                {
                    _inFlight.Remove(sessionId);
                }
            }
        }
    }

    private bool TryRead(string sessionId, out GoTrueAccessSessionStatus status)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(sessionId, out var entry))
            {
                if (entry.ExpiresAt > _time.GetUtcNow())
                {
                    status = entry.Status;
                    return true;
                }

                _cache.Remove(sessionId);
            }
        }

        status = default;
        return false;
    }

    private void Remember(string sessionId, GoTrueAccessSessionStatus status)
    {
        if (_cacheCapacity < 1 || _ttl <= TimeSpan.Zero)
        {
            return;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var sequence = ++_sequence;
            _cache[sessionId] = new CacheEntry(status, now + _ttl, sequence);
            if (_cache.Count <= _cacheCapacity)
            {
                return;
            }

            List<string>? expired = null;
            foreach (var pair in _cache)
            {
                if (pair.Value.ExpiresAt <= now)
                {
                    expired ??= [];
                    expired.Add(pair.Key);
                }
            }

            if (expired is not null)
            {
                foreach (var key in expired)
                {
                    _cache.Remove(key);
                }
            }

            while (_cache.Count > _cacheCapacity)
            {
                string? oldestKey = null;
                var oldestSequence = long.MaxValue;
                foreach (var pair in _cache)
                {
                    if (pair.Value.Sequence < oldestSequence)
                    {
                        oldestSequence = pair.Value.Sequence;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey is null)
                {
                    break;
                }

                _cache.Remove(oldestKey);
            }
        }
    }

    private readonly record struct Flight(
        Task<GoTrueAccessSessionStatus>? Shared,
        TaskCompletionSource<GoTrueAccessSessionStatus>? Created);

    private readonly record struct CacheEntry(
        GoTrueAccessSessionStatus Status,
        DateTimeOffset ExpiresAt,
        long Sequence);
}
