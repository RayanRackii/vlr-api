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
        string? clientIp,
        string scope = IPhoneVerificationSendGate.VerificationScope)
    {
        var gateScope = NormalizeScope(scope);
        var ip = NormalizeIp(clientIp);
        var now = timeProvider.GetUtcNow();

        lock (_sync)
        {
            var attempts = GetIpAttempts(gateScope, ip);
            attempts.RemoveAll(at => now - at >= IpWindow);
            if (attempts.Count >= IpMaxAttempts)
            {
                cache.Set(IpKey(gateScope, ip), attempts, IpWindow);
                return PhoneVerificationSendDecision.Limited;
            }

            if (cache.TryGetValue(CooldownKey(gateScope, tenantId, normalizedEmail), out DateTimeOffset lastSuccess)
                && now - lastSuccess < EmailCooldown)
            {
                return PhoneVerificationSendDecision.Cooldown;
            }

            attempts.Add(now);
            cache.Set(IpKey(gateScope, ip), attempts, IpWindow);
            return PhoneVerificationSendDecision.Send;
        }
    }

    public void RecordSuccess(
        Guid tenantId,
        string normalizedEmail,
        string? clientIp,
        string scope = IPhoneVerificationSendGate.VerificationScope)
    {
        _ = clientIp;
        var now = timeProvider.GetUtcNow();
        cache.Set(
            CooldownKey(NormalizeScope(scope), tenantId, normalizedEmail),
            now,
            EmailCooldown);
    }

    private List<DateTimeOffset> GetIpAttempts(string scope, string ip)
    {
        if (cache.TryGetValue(IpKey(scope, ip), out List<DateTimeOffset>? stored) && stored is not null)
        {
            return stored;
        }

        return [];
    }

    private static string NormalizeScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope)
            ? IPhoneVerificationSendGate.VerificationScope
            : scope.Trim();

    private static string NormalizeIp(string? clientIp) =>
        string.IsNullOrWhiteSpace(clientIp) ? "unknown" : clientIp.Trim();

    private static string CooldownKey(string scope, Guid tenantId, string email) =>
        $"{scope}:cooldown:{tenantId:N}:{email}";

    private static string IpKey(string scope, string ip) =>
        $"{scope}:ip:{ip}";
}
