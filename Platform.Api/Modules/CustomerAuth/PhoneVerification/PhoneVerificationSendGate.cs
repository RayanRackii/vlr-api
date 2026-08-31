using Microsoft.Extensions.Caching.Memory;

namespace Platform.Api.Modules.CustomerAuth.PhoneVerification;

public sealed class PhoneVerificationSendGate(
    IMemoryCache cache,
    TimeProvider timeProvider) : IPhoneVerificationSendGate
{
    public const int IpMaxAttempts = 10;

    public static readonly TimeSpan EmailCooldown = TimeSpan.FromSeconds(45);

    public static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(10);

    private readonly object _sync = new();

    public PhoneVerificationSendDecision Decide(
        Guid tenantId,
        string normalizedEmail,
        string? clientIp)
    {
        var ip = NormalizeIp(clientIp);
        var now = timeProvider.GetUtcNow();

        lock (_sync)
        {
            if (cache.TryGetValue(CooldownKey(tenantId, normalizedEmail), out DateTimeOffset lastSuccess)
                && now - lastSuccess < EmailCooldown)
            {
                return PhoneVerificationSendDecision.Cooldown;
            }

            var attempts = GetIpAttempts(ip);
            attempts.RemoveAll(at => now - at >= IpWindow);
            if (attempts.Count >= IpMaxAttempts)
            {
                cache.Set(IpKey(ip), attempts, IpWindow);
                return PhoneVerificationSendDecision.Limited;
            }

            attempts.Add(now);
            cache.Set(IpKey(ip), attempts, IpWindow);
            return PhoneVerificationSendDecision.Send;
        }
    }

    public void RecordSuccess(Guid tenantId, string normalizedEmail, string? clientIp)
    {
        _ = clientIp;
        var now = timeProvider.GetUtcNow();
        cache.Set(CooldownKey(tenantId, normalizedEmail), now, EmailCooldown);
    }

    private List<DateTimeOffset> GetIpAttempts(string ip)
    {
        if (cache.TryGetValue(IpKey(ip), out List<DateTimeOffset>? stored) && stored is not null)
        {
            return stored;
        }

        return [];
    }

    private static string NormalizeIp(string? clientIp) =>
        string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp.Trim();

    private static string CooldownKey(Guid tenantId, string email) =>
        $"phone-verify:cooldown:{tenantId:N}:{email}";

    private static string IpKey(string ip) =>
        $"phone-verify:ip:{ip}";
}
