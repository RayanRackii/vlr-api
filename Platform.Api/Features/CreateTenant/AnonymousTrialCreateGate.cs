using Microsoft.Extensions.Caching.Memory;

namespace Platform.Api.Features.CreateTenant;

/// <summary>
/// Per-IP window for anonymous trial creation. Same shape as
/// <c>PhoneVerificationSendGate</c>'s IP bucket: in-memory timestamps, 10 attempts,
/// 10 minutes, blank addresses share <c>unknown</c>. No email cooldown.
/// </summary>
public sealed class AnonymousTrialCreateGate(
    IMemoryCache cache,
    TimeProvider timeProvider) : IAnonymousTrialCreateGate
{
    public const int IpMaxAttempts = 10;

    public static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();

    public bool TryConsume(string? clientIp)
    {
        var ip = string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp.Trim();
        var now = timeProvider.GetUtcNow();

        lock (_sync)
        {
            var attempts = GetAttempts(ip);
            attempts.RemoveAll(at => now - at >= IpWindow);
            if (attempts.Count >= IpMaxAttempts)
            {
                cache.Set(Key(ip), attempts, IpWindow);
                return false;
            }

            attempts.Add(now);
            cache.Set(Key(ip), attempts, IpWindow);
            return true;
        }
    }

    private List<DateTimeOffset> GetAttempts(string ip)
    {
        if (cache.TryGetValue(Key(ip), out List<DateTimeOffset>? stored) && stored is not null)
        {
            return stored;
        }

        return [];
    }

    private static string Key(string ip) => $"anonymous-trial:ip:{ip}";
}
