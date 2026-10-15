using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Platform.Api.Features.CreateTenant;
using Platform.Api.Http;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;

namespace Platform.Api.Tests.Http;

public sealed class RateLimitClientIpTests
{
    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("  203.0.113.5  ", "203.0.113.5")]
    [InlineData("203.0.113.005", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("2001:0db8:0000:0000:0000:0000:0000:0001", "2001:db8::1")]
    [InlineData("[2001:db8::1]", "2001:db8::1")]
    [InlineData("fe80::1%1", "fe80::1")]
    [InlineData("fe80::1%2", "fe80::1")]
    [InlineData("1", "0.0.0.1")]
    [InlineData("127.1", "127.0.0.1")]
    public void Valid_real_ip_is_the_canonical_address_and_ignores_spoofed_headers(
        string realIp,
        string expected)
    {
        var context = Request(
            realIp,
            forwardedFor: "198.51.100.8, 10.0.0.1",
            connectingIp: "198.51.100.9",
            remote: "10.0.0.8");

        Assert.Equal(expected, RateLimitClientIp.From(context));
    }

    [Fact]
    public void Real_ip_header_name_is_case_insensitive()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-real-ip"] = "203.0.113.5";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.8";
        context.Request.Headers["CF-Connecting-IP"] = "198.51.100.9";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");

        Assert.Equal("203.0.113.5", RateLimitClientIp.From(context));
    }

    [Fact]
    public void Spoofed_forwarded_for_is_not_the_client()
    {
        var context = Request(
            realIp: null,
            forwardedFor: "203.0.113.5, 10.0.0.1",
            connectingIp: null,
            remote: "198.51.100.20");

        Assert.Equal("198.51.100.20", RateLimitClientIp.From(context));
    }

    [Fact]
    public void Spoofed_cf_connecting_ip_is_not_the_client()
    {
        var context = Request(
            realIp: null,
            forwardedFor: null,
            connectingIp: "203.0.113.5",
            remote: "198.51.100.21");

        Assert.Equal("198.51.100.21", RateLimitClientIp.From(context));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.5, 198.51.100.1")]
    [InlineData("203.0.113.5:443")]
    [InlineData("203.0.113.5\n198.51.100.1")]
    [InlineData("999.1.1.1")]
    public void Missing_or_malformed_real_ip_uses_the_remote_address_not_the_spoofed_headers(
        string? realIp)
    {
        var context = Request(
            realIp,
            forwardedFor: "203.0.113.5, 10.0.0.1",
            connectingIp: "198.51.100.77",
            remote: "192.0.2.10");

        Assert.Equal("192.0.2.10", RateLimitClientIp.From(context));
    }

    [Fact]
    public void Repeated_real_ip_header_is_malformed_and_uses_the_remote_address()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Real-IP"] = new StringValues(["203.0.113.5", "198.51.100.8"]);
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.10";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.11");

        Assert.Equal("192.0.2.11", RateLimitClientIp.From(context));
    }

    [Fact]
    public void Mapped_remote_address_is_canonical_when_the_header_is_missing()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.40");

        Assert.Equal("192.0.2.40", RateLimitClientIp.From(context));
    }

    [Fact]
    public void Null_context_has_no_partition_key()
    {
        Assert.Null(RateLimitClientIp.From(null));
    }

    [Fact]
    public void Rotating_malformed_headers_share_one_limiter_bucket()
    {
        var sendGate = new PhoneVerificationSendGate(
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System);
        var trialGate = new AnonymousTrialCreateGate(
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System);
        var tenantId = Guid.NewGuid();

        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            var key = RateLimitClientIp.From(Request(
                realIp: $"not-a-real-ip-{i}",
                forwardedFor: $"203.0.113.{i}",
                connectingIp: $"198.51.100.{i}",
                remote: "192.0.2.10"));
            Assert.Equal("192.0.2.10", key);
            Assert.Equal(
                PhoneVerificationSendDecision.Send,
                sendGate.Decide(tenantId, $"user{i}@club.test", key));
            Assert.True(trialGate.TryConsume(key));
        }

        var limited = RateLimitClientIp.From(Request(
            realIp: "still-not-an-ip",
            forwardedFor: "203.0.113.200, 10.0.0.1",
            connectingIp: "198.51.100.200",
            remote: "192.0.2.10"));
        Assert.Equal(
            PhoneVerificationSendDecision.Limited,
            sendGate.Decide(tenantId, "eleventh@club.test", limited));
        Assert.False(trialGate.TryConsume(limited));
    }

    [Fact]
    public void Missing_socket_address_still_shares_one_limiter_bucket()
    {
        var sendGate = new PhoneVerificationSendGate(
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System);
        var trialGate = new AnonymousTrialCreateGate(
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System);
        var tenantId = Guid.NewGuid();

        for (var i = 0; i < PhoneVerificationSendGate.IpMaxAttempts; i++)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Real-IP"] = $"garbage-{i}";
            context.Request.Headers["X-Forwarded-For"] = $"203.0.113.{i}";
            context.Request.Headers["CF-Connecting-IP"] = $"198.51.100.{i}";
            var key = RateLimitClientIp.From(context);
            Assert.Null(key);
            Assert.Equal(
                PhoneVerificationSendDecision.Send,
                sendGate.Decide(tenantId, $"anon{i}@club.test", key));
            Assert.True(trialGate.TryConsume(key));
        }

        var again = new DefaultHttpContext();
        again.Request.Headers["X-Forwarded-For"] = "203.0.113.50";
        again.Request.Headers["CF-Connecting-IP"] = "198.51.100.50";
        Assert.Equal(
            PhoneVerificationSendDecision.Limited,
            sendGate.Decide(tenantId, "last@club.test", RateLimitClientIp.From(again)));
        Assert.False(trialGate.TryConsume(RateLimitClientIp.From(again)));
    }

    [Fact]
    public void Production_code_does_not_trust_forwarded_headers_for_client_identity()
    {
        var apiDir = Path.Combine(FindRepoRoot(), "Platform.Api");
        var files = Directory.GetFiles(apiDir, "*.cs", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("\"X-Forwarded-For\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"CF-Connecting-IP\"", text, StringComparison.Ordinal);
            Assert.DoesNotContain("UseForwardedHeaders", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ForwardedHeadersOptions", text, StringComparison.Ordinal);
            Assert.DoesNotContain("KnownProxies", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ForwardLimit", text, StringComparison.Ordinal);
        }

        var readers = files
            .Where(file => File.ReadAllText(file).Contains("\"X-Real-IP\"", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .ToArray();
        Assert.Equal(["RateLimitClientIp.cs"], readers);
    }

    [Fact]
    public void Rate_limit_call_sites_use_the_shared_reader()
    {
        var root = FindRepoRoot();
        foreach (var relative in new[]
        {
            Path.Combine("Platform.Api", "Modules", "Auth", "Services", "PasswordRecoveryService.cs"),
            Path.Combine("Platform.Api", "Modules", "CustomerAuth", "Services", "CustomerAuthService.cs"),
            Path.Combine("Platform.Api", "Features", "CreateTenant", "CreateTenantEndpoint.cs"),
        })
        {
            var text = File.ReadAllText(Path.Combine(root, relative));
            Assert.Contains("RateLimitClientIp.From", text, StringComparison.Ordinal);
        }
    }

    private static DefaultHttpContext Request(
        string? realIp,
        string? forwardedFor,
        string? connectingIp,
        string remote)
    {
        var context = new DefaultHttpContext();
        if (realIp is not null)
        {
            context.Request.Headers["X-Real-IP"] = realIp;
        }

        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        if (connectingIp is not null)
        {
            context.Request.Headers["CF-Connecting-IP"] = connectingIp;
        }

        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        return context;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Core", "Platform.Core.Infrastructure")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate vlr-api repository root.");
    }
}
