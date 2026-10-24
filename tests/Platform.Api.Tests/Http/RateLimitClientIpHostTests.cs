using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Api.Http;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Http;

public sealed class RateLimitClientIpHostTests
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly string[] IsolatedVariables =
    [
        "ASPNETCORE_ENVIRONMENT",
        "ConnectionStrings__DefaultConnection",
        "Supabase__Url",
        "Supabase__JwtSecret",
        "RateLimit__ClientIpHeader",
        "FLY_APP_NAME",
        "RAILWAY_ENVIRONMENT",
        "RAILWAY_PROJECT_ID",
        "Tenancy__FailClosed",
    ];

    [Theory]
    [InlineData(null, false)] // default off: no appsettings/fly.toml sets it
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Real_host_binds_tenancy_fail_closed(string? value, bool expected)
    {
        using var scope = new HostEnvironmentScope(("Tenancy__FailClosed", value!));

        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        Assert.Equal(
            expected,
            factory.Services.GetRequiredService<IOptions<TenancyOptions>>().Value.FailClosed);
    }

    [Fact]
    public void Real_host_rejects_a_header_outside_the_allowlist()
    {
        using var scope = new HostEnvironmentScope(
            ("RateLimit__ClientIpHeader", "X-Forwarded-For"));

        using var factory = new WebApplicationFactory<Program>();
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = Flatten(ex);
        Assert.Contains("RateLimit:ClientIpHeader", message, StringComparison.Ordinal);
        Assert.Contains("X-Real-IP", message, StringComparison.Ordinal);
        Assert.Contains("Fly-Client-IP", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_host_honours_fly_client_ip()
    {
        using var scope = new HostEnvironmentScope(
            ("RateLimit__ClientIpHeader", "Fly-Client-IP"));

        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        Assert.NotNull(client);

        Assert.Equal(
            "Fly-Client-IP",
            factory.Services.GetRequiredService<IOptions<RateLimitOptions>>().Value.ClientIpHeader);

        var context = new DefaultHttpContext
        {
            RequestServices = factory.Services,
        };
        context.Request.Headers["Fly-Client-IP"] = "2001:db8::5";
        context.Request.Headers["X-Real-IP"] = "203.0.113.5";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.8";
        context.Request.Headers["CF-Connecting-IP"] = "198.51.100.9";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.9");

        Assert.Equal("2001:db8::5", RateLimitClientIp.From(context));
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(Environment.NewLine, messages);
    }

    internal sealed class HostEnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];

        public HostEnvironmentScope(params (string Name, string Value)[] overrides)
        {
            Gate.Wait();
            foreach (var name in IsolatedVariables)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
            }

            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Test");
            Environment.SetEnvironmentVariable(
                "ConnectionStrings__DefaultConnection",
                "Host=127.0.0.1;Port=5432;Database=unused;Username=unused;Password=unused");
            Environment.SetEnvironmentVariable("Supabase__Url", "https://example.supabase.co");
            Environment.SetEnvironmentVariable(
                "Supabase__JwtSecret",
                "test-secret-test-secret-test-secret");
            Environment.SetEnvironmentVariable("FLY_APP_NAME", "");
            Environment.SetEnvironmentVariable("RAILWAY_ENVIRONMENT", "");
            Environment.SetEnvironmentVariable("RAILWAY_PROJECT_ID", "");
            Environment.SetEnvironmentVariable("RateLimit__ClientIpHeader", null);
            Environment.SetEnvironmentVariable("Tenancy__FailClosed", null);
            foreach (var (name, value) in overrides)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            try
            {
                foreach (var (name, value) in _previous)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }
            }
            finally
            {
                Gate.Release();
            }
        }
    }
}
