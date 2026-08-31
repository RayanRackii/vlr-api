using Microsoft.Extensions.Caching.Memory;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Api.Tests.Infrastructure;

namespace Platform.Api.Tests.CustomerAuth;

public sealed class PhoneVerificationSendGateTests
{
    [Fact]
    public void Decide_returns_cooldown_after_successful_start()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));
        var sut = CreateSut(time);
        var tenantId = Guid.NewGuid();
        var email = "same@club.test";

        Assert.Equal(PhoneVerificationSendDecision.Send, sut.Decide(tenantId, email, "1.1.1.1"));
        sut.RecordSuccess(tenantId, email, "1.1.1.1");

        Assert.Equal(PhoneVerificationSendDecision.Cooldown, sut.Decide(tenantId, email, "1.1.1.1"));
    }

    [Fact]
    public void Cooldown_expires_after_45_seconds()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));
        var sut = CreateSut(time);
        var tenantId = Guid.NewGuid();
        var email = "later@club.test";

        Assert.Equal(PhoneVerificationSendDecision.Send, sut.Decide(tenantId, email, "2.2.2.2"));
        sut.RecordSuccess(tenantId, email, "2.2.2.2");
        time.Advance(TimeSpan.FromSeconds(46));

        Assert.Equal(PhoneVerificationSendDecision.Send, sut.Decide(tenantId, email, "2.2.2.2"));
    }

    [Fact]
    public void Ip_limit_is_ten_attempts_in_ten_minutes()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));
        var sut = CreateSut(time);
        var ip = "9.9.9.9";

        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            Assert.Equal(
                PhoneVerificationSendDecision.Send,
                sut.Decide(Guid.NewGuid(), $"user{i}@club.test", ip));
        }

        Assert.Equal(
            PhoneVerificationSendDecision.Limited,
            sut.Decide(Guid.NewGuid(), "blocked@club.test", ip));
    }

    [Fact]
    public void Ip_limit_wins_over_email_cooldown()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));
        var sut = CreateSut(time);
        var ip = "8.8.8.8";
        var tenantId = Guid.NewGuid();
        var email = "cooled@club.test";

        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts - 1; i++)
        {
            Assert.Equal(
                PhoneVerificationSendDecision.Send,
                sut.Decide(Guid.NewGuid(), $"other{i}@club.test", ip));
        }

        Assert.Equal(PhoneVerificationSendDecision.Send, sut.Decide(tenantId, email, ip));
        sut.RecordSuccess(tenantId, email, ip);

        Assert.Equal(PhoneVerificationSendDecision.Limited, sut.Decide(tenantId, email, ip));
    }

    [Fact]
    public void Missing_ip_shares_unknown_bucket()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));
        var sut = CreateSut(time);

        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            Assert.Equal(
                PhoneVerificationSendDecision.Send,
                sut.Decide(Guid.NewGuid(), $"anon{i}@club.test", clientIp: null));
        }

        Assert.Equal(
            PhoneVerificationSendDecision.Limited,
            sut.Decide(Guid.NewGuid(), "unknown-bucket@club.test", clientIp: " "));
    }

    private static PhoneVerificationSendGate CreateSut(TimeProvider time) =>
        new(new MemoryCache(new MemoryCacheOptions()), time);
}
