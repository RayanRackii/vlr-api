using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Platform.Api.Authorization;
using Platform.Api.Jobs;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Tenancy;

/// <summary>
/// Passo 5 (2/2): cross-tenant jobs run their sweep inside a PlatformScope,
/// and the scope never outlives the run.
/// ReservationReminderJob is covered in ReservationReminderJobTests (per-tenant work stays tenant).
/// </summary>
public sealed class PlatformScopeJobTests
{
    [Fact]
    public async Task PmocEngineJob_sweeps_in_platform_scope()
    {
        var logger = new ModeRecordingLogger<PmocEngineJob>();
        var job = new PmocEngineJob(
            InMemoryAppDb.Create(new FakeTenantProvider()),
            null!, // no plans seeded, so generation is never reached
            TimeProvider.System,
            logger);

        await AssertPlatformSweepAsync(logger, () => job.RunAsync(CancellationToken.None), "PmocEngineJob finished");
    }

    [Fact]
    public async Task NotificationOutboxJob_sweeps_in_platform_scope()
    {
        var logger = new ModeRecordingLogger<NotificationOutboxJob>();
        var processor = new ModeRecordingOutboxProcessor();
        var job = new NotificationOutboxJob(processor, logger);

        await AssertPlatformSweepAsync(logger, () => job.ExecuteAsync(CancellationToken.None), "Sweeping notification outbox");
        Assert.Equal([TenantContextMode.Platform, TenantContextMode.Platform], processor.Modes);
    }

    [Fact]
    public async Task TrialLifecycleJob_sweeps_in_platform_scope()
    {
        var logger = new ModeRecordingLogger<TrialLifecycleJob>();
        var job = new TrialLifecycleJob(
            InMemoryAppDb.Create(new FakeTenantProvider()),
            null!, // no trial tenant is due, so DeleteAsync is never reached
            logger);

        await AssertPlatformSweepAsync(logger, () => job.ExecuteAsync(CancellationToken.None), "No trial tenants due for purge");
    }

    [Fact]
    public async Task DataRetentionJob_sweeps_in_platform_scope()
    {
        var logger = new ModeRecordingLogger<DataRetentionJob>();
        var job = new DataRetentionJob(InMemoryAppDb.Create(new FakeTenantProvider()), logger);

        await AssertPlatformSweepAsync(logger, () => job.ExecuteAsync(CancellationToken.None), "DataRetentionJob completed");
    }

    [Fact]
    public async Task ClientRoleDiagnosticHostedService_scans_in_platform_scope()
    {
        var databaseName = $"client-role-{Guid.NewGuid():N}";
        await using var services = new ServiceCollection()
            .AddScoped<AppDbContext>(_ => InMemoryAppDb.Create(new FakeTenantProvider(), databaseName))
            .BuildServiceProvider();
        var logger = new ModeRecordingLogger<ClientRoleDiagnosticHostedService>();
        var service = new ClientRoleDiagnosticHostedService(
            services.GetRequiredService<IServiceScopeFactory>(),
            logger);

        await AssertPlatformSweepAsync(logger, () => service.StartAsync(CancellationToken.None), "RBAC Client role diagnostic");
    }

    /// <summary>Runs the job twice: each run is Platform during the sweep and None before/after.</summary>
    private static async Task AssertPlatformSweepAsync(
        ModeRecordingLogger logger,
        Func<Task> run,
        string sweepMessage)
    {
        var context = new TenantContext(new FakeTenantProvider());

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(TenantContextMode.None, context.Mode);
            logger.Entries.Clear();

            await run();

            Assert.Equal(TenantContextMode.None, context.Mode);
            Assert.False(PlatformScope.IsActive);

            // Begin logs before the scope becomes current, so Reason here is the parent scope.
            var entered = Assert.Single(logger.Entries, entry => entry.Message.StartsWith("Entering platform scope", StringComparison.Ordinal));
            Assert.Null(entered.Reason);
            var sweep = Assert.Single(logger.Entries, entry => entry.Message.Contains(sweepMessage, StringComparison.Ordinal));
            Assert.Equal(TenantContextMode.Platform, sweep.Mode);
        }
    }

    private sealed class ModeRecordingOutboxProcessor : INotificationOutboxProcessor
    {
        private readonly TenantContext _context = new(new FakeTenantProvider());

        public List<TenantContextMode> Modes { get; } = [];

        public Task ProcessDueAsync(CancellationToken cancellationToken = default)
        {
            Modes.Add(_context.Mode);
            return Task.CompletedTask;
        }

        public Task ProcessDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

internal sealed record ModeRecordedLog(string Message, TenantContextMode Mode, string? Reason);

/// <summary>Records the ITenantContext mode (and current PlatformScope reason) at each log call.</summary>
internal class ModeRecordingLogger : ILogger
{
    private readonly TenantContext _context = new(new FakeTenantProvider());

    public List<ModeRecordedLog> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(new ModeRecordedLog(formatter(state, exception), _context.Mode, PlatformScope.CurrentReason));
}

internal sealed class ModeRecordingLogger<T> : ModeRecordingLogger, ILogger<T>;
