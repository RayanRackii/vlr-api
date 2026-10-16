using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
            .Where(file => File.ReadAllText(file).Contains("\"X-Real-IP\"", StringComparison.Ordinal)
                || File.ReadAllText(file).Contains("\"Fly-Client-IP\"", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .Distinct()
            .Order(StringComparer.Ordinal)
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
    public void Configured_fly_header_is_canonical_and_ignores_spoofed_real_ip(
        string flyClientIp,
        string expected)
    {
        var context = Request(
            realIp: "198.51.100.50",
            forwardedFor: "203.0.113.9, 10.0.0.1",
            connectingIp: "198.51.100.9",
            remote: "10.0.0.8",
            flyClientIp: flyClientIp);

        Assert.Equal(expected, Read(context, "Fly-Client-IP"));
    }

    [Theory]
    [InlineData("X-Real-IP")]
    [InlineData("x-real-ip")]
    [InlineData("  X-Real-IP  ")]
    public void Configured_real_ip_ignores_spoofed_fly_client_ip(string configuredHeader)
    {
        var context = Request(
            realIp: "2001:0db8:0000:0000:0000:0000:0000:0001",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "10.0.0.8",
            flyClientIp: "203.0.113.77");

        Assert.Equal("2001:db8::1", Read(context, configuredHeader));
    }

    [Fact]
    public void Fly_header_name_is_case_insensitive()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["fly-client-ip"] = "2001:db8::2";
        context.Request.Headers["X-Real-IP"] = "203.0.113.5";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.8";
        context.Request.Headers["CF-Connecting-IP"] = "198.51.100.9";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");

        Assert.Equal("2001:db8::2", Read(context, "fly-client-ip"));
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
    public void Malformed_fly_header_uses_the_remote_address_and_ignores_spoofed_headers(
        string? flyClientIp)
    {
        var context = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8, 10.0.0.1",
            connectingIp: "198.51.100.77",
            remote: "192.0.2.10",
            flyClientIp: flyClientIp);

        Assert.Equal("192.0.2.10", Read(context, "Fly-Client-IP"));
    }

    [Fact]
    public void Repeated_fly_header_uses_the_remote_address()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Fly-Client-IP"] = new StringValues(["203.0.113.5", "198.51.100.8"]);
        context.Request.Headers["X-Real-IP"] = "203.0.113.9";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.11";
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.11");

        Assert.Equal("192.0.2.11", Read(context, "Fly-Client-IP"));
    }

    [Fact]
    public void Private_remote_without_fly_header_ignores_spoofed_forwarded_headers()
    {
        var context = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "fd00::1",
            flyClientIp: "not-an-ip");

        Assert.Equal("fd00::1", Read(context, "Fly-Client-IP"));
    }

    [Fact]
    public void Private_remote_without_real_ip_ignores_spoofed_fly_header()
    {
        var context = Request(
            realIp: "   ",
            forwardedFor: "203.0.113.5, 10.0.0.1",
            connectingIp: "198.51.100.9",
            remote: "10.1.2.3",
            flyClientIp: "2001:db8::9");

        Assert.Equal("10.1.2.3", Read(context, "X-Real-IP"));
    }

    [Theory]
    [InlineData("Fly-Client-IP", "203.0.113.15")]
    [InlineData("X-Real-IP", "2001:db8::15")]
    public void Selected_header_is_kept_when_the_socket_address_is_private(
        string configuredHeader,
        string clientIp)
    {
        var context = Request(
            realIp: configuredHeader == "X-Real-IP" ? clientIp : "198.51.100.1",
            forwardedFor: "198.51.100.2",
            connectingIp: "198.51.100.3",
            remote: "172.16.0.4",
            flyClientIp: configuredHeader == "Fly-Client-IP" ? clientIp : "198.51.100.4");

        Assert.Equal(clientIp, Read(context, configuredHeader));
    }

    [Fact]
    public void Disallowed_header_name_does_not_read_forwarded_headers()
    {
        var context = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "192.0.2.30",
            flyClientIp: "203.0.113.6");

        Assert.Equal("192.0.2.30", Read(context, "X-Forwarded-For"));
        Assert.Equal("192.0.2.30", Read(context, "CF-Connecting-IP"));
    }

    [Fact]
    public void Blank_option_keeps_the_real_ip_header()
    {
        var context = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "10.0.0.8",
            flyClientIp: "198.51.100.20");

        Assert.Equal("203.0.113.5", Read(context, "   "));
    }

    [Fact]
    public void Rotating_spoofed_real_ip_shares_the_fly_remote_bucket()
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
            var context = Request(
                realIp: $"203.0.113.{i}",
                forwardedFor: $"198.51.100.{i}",
                connectingIp: $"192.0.2.{i}",
                remote: "10.8.0.4",
                flyClientIp: $"garbage-{i}");
            var key = Read(context, "Fly-Client-IP");
            Assert.Equal("10.8.0.4", key);
            Assert.Equal(
                PhoneVerificationSendDecision.Send,
                sendGate.Decide(tenantId, $"user{i}@club.test", key));
            Assert.True(trialGate.TryConsume(key));
        }

        var limited = Read(
            Request(
                realIp: "203.0.113.200",
                forwardedFor: "198.51.100.200",
                connectingIp: "192.0.2.200",
                remote: "10.8.0.4",
                flyClientIp: "still-not-an-ip"),
            "Fly-Client-IP");
        Assert.Equal(
            PhoneVerificationSendDecision.Limited,
            sendGate.Decide(tenantId, "eleventh@club.test", limited));
        Assert.False(trialGate.TryConsume(limited));
    }

    [Fact]
    public void Missing_option_binds_the_default_header()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddRateLimitClientIp(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            "X-Real-IP",
            provider.GetRequiredService<IOptions<RateLimitOptions>>().Value.ClientIpHeader);
    }

    [Theory]
    [InlineData(null, "203.0.113.5")]
    [InlineData("", "203.0.113.5")]
    [InlineData("   ", "203.0.113.5")]
    [InlineData("X-Real-IP", "203.0.113.5")]
    [InlineData("fly-client-ip", "2001:db8::7")]
    public async Task Host_uses_only_the_allowed_header(string? clientIpHeader, string expected)
    {
        using var host = await StartHostAsync(clientIpHeader);
        var context = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "10.0.0.4",
            flyClientIp: "2001:db8::7");
        context.RequestServices = host.Services;
        Assert.Equal(expected, RateLimitClientIp.From(context));
    }

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("CF-Connecting-IP")]
    [InlineData("X-Real-IP, Fly-Client-IP")]
    [InlineData("True-Client-IP")]
    public async Task Host_rejects_a_header_outside_the_allowlist(string clientIpHeader)
    {
        var builder = HostBuilder(clientIpHeader);
        builder.Services.AddRateLimitClientIp(builder.Configuration);
        using var host = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("RateLimit:ClientIpHeader", ex.Message, StringComparison.Ordinal);
        Assert.Contains("X-Real-IP", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Fly-Client-IP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Started_host_uses_only_the_configured_header()
    {
        using var flyHost = await StartHostAsync("Fly-Client-IP");
        var flyContext = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8, 10.0.0.1",
            connectingIp: "198.51.100.9",
            remote: "172.16.0.8",
            flyClientIp: "2001:db8::8");
        flyContext.RequestServices = flyHost.Services;
        Assert.Equal("2001:db8::8", RateLimitClientIp.From(flyContext));

        using var railwayHost = await StartHostAsync(null);
        var railwayContext = Request(
            realIp: "::ffff:203.0.113.5",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "10.0.0.8",
            flyClientIp: "2001:db8::8");
        railwayContext.RequestServices = railwayHost.Services;
        Assert.Equal("203.0.113.5", RateLimitClientIp.From(railwayContext));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("X-Real-IP")]
    public async Task Fly_without_fly_client_ip_does_not_start(string? clientIpHeader)
    {
        var ex = await AssertStartFails(
            clientIpHeader,
            new Dictionary<string, string?> { ["FLY_APP_NAME"] = "rolvix" });
        Assert.Contains("Fly", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FLY_APP_NAME", ex.Message, StringComparison.Ordinal);
        Assert.Contains("X-Real-IP", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Fly-Client-IP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fly_with_fly_client_ip_starts_and_ignores_spoofed_real_ip()
    {
        using var host = await StartHostAsync(
            "fly-client-ip",
            new Dictionary<string, string?> { ["FLY_APP_NAME"] = "rolvix" });
        var context = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "172.16.0.8",
            flyClientIp: "2001:db8::8");
        context.RequestServices = host.Services;
        Assert.Equal("2001:db8::8", RateLimitClientIp.From(context));
    }

    [Theory]
    [InlineData("RAILWAY_ENVIRONMENT", "production")]
    [InlineData("RAILWAY_PROJECT_ID", "project")]
    public async Task Railway_with_fly_client_ip_does_not_start(string variable, string value)
    {
        var ex = await AssertStartFails(
            "fly-client-ip",
            new Dictionary<string, string?> { [variable] = value });
        Assert.Contains("Railway", ex.Message, StringComparison.Ordinal);
        Assert.Contains(variable, ex.Message, StringComparison.Ordinal);
        Assert.Contains("Fly-Client-IP", ex.Message, StringComparison.Ordinal);
        Assert.Contains("X-Real-IP", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("RAILWAY_ENVIRONMENT")]
    [InlineData("RAILWAY_PROJECT_ID")]
    public async Task Railway_with_the_default_header_starts_and_ignores_spoofed_fly_ip(string variable)
    {
        using var host = await StartHostAsync(
            null,
            new Dictionary<string, string?> { [variable] = "production" });
        var context = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "10.0.0.8",
            flyClientIp: "2001:db8::8");
        context.RequestServices = host.Services;
        Assert.Equal("203.0.113.5", RateLimitClientIp.From(context));
    }

    [Theory]
    [InlineData(null, "203.0.113.5")]
    [InlineData("Fly-Client-IP", "2001:db8::7")]
    public async Task No_platform_env_keeps_the_configured_header(string? clientIpHeader, string expected)
    {
        using var host = await StartHostAsync(
            clientIpHeader,
            new Dictionary<string, string?>
            {
                ["FLY_APP_NAME"] = "   ",
                ["RAILWAY_ENVIRONMENT"] = "",
                ["RAILWAY_PROJECT_ID"] = null,
            });
        var context = Request(
            realIp: "203.0.113.5",
            forwardedFor: "198.51.100.8",
            connectingIp: "198.51.100.9",
            remote: "10.0.0.4",
            flyClientIp: "2001:db8::7");
        context.RequestServices = host.Services;
        Assert.Equal(expected, RateLimitClientIp.From(context));
    }

    [Theory]
    [InlineData(null, "Fly")]
    [InlineData("X-Real-IP", "Fly")]
    [InlineData("Fly-Client-IP", "Railway")]
    public async Task Both_platform_signals_fail_startup(string? clientIpHeader, string mismatchedPlatform)
    {
        var ex = await AssertStartFails(
            clientIpHeader,
            new Dictionary<string, string?>
            {
                ["FLY_APP_NAME"] = "rolvix",
                ["RAILWAY_ENVIRONMENT"] = "production",
            });
        Assert.Contains($"{mismatchedPlatform} is detected", ex.Message, StringComparison.Ordinal);
    }

    private static string? Read(DefaultHttpContext context, string configuredHeader)
    {
        var services = new ServiceCollection();
        services.Configure<RateLimitOptions>(options => options.ClientIpHeader = configuredHeader);
        using var provider = services.BuildServiceProvider();
        context.RequestServices = provider;
        return RateLimitClientIp.From(context);
    }

    private static HostApplicationBuilder HostBuilder(
        string? clientIpHeader,
        IReadOnlyDictionary<string, string?>? platform = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            ApplicationName = "rate-limit-client-ip-tests",
        });
        var values = new Dictionary<string, string?>
        {
            ["FLY_APP_NAME"] = "",
            ["RAILWAY_ENVIRONMENT"] = "",
            ["RAILWAY_PROJECT_ID"] = "",
        };
        values[$"{RateLimitOptions.SectionName}:ClientIpHeader"] = clientIpHeader ?? "";

        if (platform is not null)
        {
            foreach (var pair in platform)
            {
                values[pair.Key] = pair.Value;
            }
        }

        builder.Configuration.AddInMemoryCollection(values);
        return builder;
    }

    private static Task<IHost> StartHostAsync(string? clientIpHeader) =>
        StartHostAsync(clientIpHeader, platform: null);

    private static async Task<OptionsValidationException> AssertStartFails(
        string? clientIpHeader,
        IReadOnlyDictionary<string, string?> platform)
    {
        var builder = HostBuilder(clientIpHeader, platform);
        builder.Services.AddRateLimitClientIp(builder.Configuration);
        using var host = builder.Build();
        return await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }

    private static async Task<IHost> StartHostAsync(
        string? clientIpHeader,
        IReadOnlyDictionary<string, string?>? platform)
    {
        var builder = HostBuilder(clientIpHeader, platform);
        builder.Services.AddRateLimitClientIp(builder.Configuration);
        var host = builder.Build();
        try
        {
            await host.StartAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static DefaultHttpContext Request(
        string? realIp,
        string? forwardedFor,
        string? connectingIp,
        string remote,
        string? flyClientIp = null)
    {
        var context = new DefaultHttpContext();
        if (realIp is not null)
        {
            context.Request.Headers["X-Real-IP"] = realIp;
        }

        if (flyClientIp is not null)
        {
            context.Request.Headers["Fly-Client-IP"] = flyClientIp;
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
