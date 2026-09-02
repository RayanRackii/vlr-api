using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Api.Storage;
using Platform.Api.Tests.Infrastructure;

namespace Platform.Api.Tests.Storage;

public sealed class StorageServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStorage_with_credentials_registers_supabase_provider()
    {
        using var provider = BuildProvider(
            "Development",
            supabaseUrl: "https://example.supabase.co",
            serviceRoleKey: "service-role-not-a-real-key");

        Assert.Equal("SupabaseStorageProvider", provider.GetRequiredService<IStorageProvider>().GetType().Name);
    }

    [Fact]
    public void AddStorage_without_credentials_registers_dev_provider()
    {
        using var provider = BuildProvider("Development");

        Assert.Equal("DevStorageProvider", provider.GetRequiredService<IStorageProvider>().GetType().Name);
    }

    [Fact]
    public async Task Production_without_credentials_logs_error_for_dev_storage_provider()
    {
        const string secretKey = "service-role-not-a-real-key";
        var logs = await CaptureHostedStartLogsAsync(
            "Production",
            supabaseUrl: null,
            serviceRoleKey: secretKey);

        using var provider = BuildProvider("Production", serviceRoleKey: secretKey);
        Assert.Equal("DevStorageProvider", provider.GetRequiredService<IStorageProvider>().GetType().Name);
        Assert.Contains(
            logs,
            log => log.Level == LogLevel.Error
                && log.Message.Contains("DevStorageProvider", StringComparison.Ordinal)
                && log.Message.Contains("Production", StringComparison.Ordinal)
                && (log.Message.Contains("Storage:SupabaseUrl", StringComparison.Ordinal)
                    || log.Message.Contains("Storage:ServiceRoleKey", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            logs,
            log => log.Message.Contains(secretKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Development_without_credentials_does_not_log_production_error()
    {
        using var provider = BuildProvider("Development");
        Assert.Equal("DevStorageProvider", provider.GetRequiredService<IStorageProvider>().GetType().Name);

        var logs = await CaptureHostedStartLogsAsync("Development");

        Assert.DoesNotContain(
            logs,
            log => log.Level == LogLevel.Error
                && log.Message.Contains("Production", StringComparison.Ordinal));
    }

    private static ServiceProvider BuildProvider(
        string environmentName,
        string? supabaseUrl = null,
        string? serviceRoleKey = null,
        CapturingLoggerProvider? capturingLogger = null)
    {
        var values = new Dictionary<string, string?>();
        if (supabaseUrl is not null)
        {
            values["Storage:SupabaseUrl"] = supabaseUrl;
        }

        if (serviceRoleKey is not null)
        {
            values["Storage:ServiceRoleKey"] = serviceRoleKey;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var hostEnvironment = new FakeHostEnvironment(environmentName);
        var services = new ServiceCollection();
        if (capturingLogger is not null)
        {
            services.AddLogging(builder => builder.AddProvider(capturingLogger));
        }
        else
        {
            services.AddLogging();
        }

        services.AddOptions();
        services.AddSingleton<IHostEnvironment>(hostEnvironment);
        services.AddStorage(configuration, hostEnvironment);
        return services.BuildServiceProvider();
    }

    private static async Task<IReadOnlyList<CapturedLog>> CaptureHostedStartLogsAsync(
        string environmentName,
        string? supabaseUrl = null,
        string? serviceRoleKey = null)
    {
        var capturingLogger = new CapturingLoggerProvider();
        await using var provider = BuildProvider(
            environmentName,
            supabaseUrl,
            serviceRoleKey,
            capturingLogger);
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
        }

        try
        {
            return capturingLogger.Entries;
        }
        finally
        {
            foreach (var service in hosted)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Platform.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
