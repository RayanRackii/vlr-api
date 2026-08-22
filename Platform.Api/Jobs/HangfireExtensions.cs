using Hangfire;
using Hangfire.PostgreSql;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Jobs;

public static class HangfireExtensions
{
    public const string DataRetentionJobId = "data-retention-gc";

    public const string PmocEngineJobId = "pmoc-engine";

    public const string TrialLifecycleJobId = "trial-lifecycle-purge";

    private const int HangfireMaxPoolSize = 3;

    public static IServiceCollection AddPlatformHangfire(
        this IServiceCollection services,
        string connectionString)
    {
        var bounded = NpgsqlConnectionStringHelper.WithBoundedPoolSize(
            connectionString,
            HangfireMaxPoolSize);

        services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(bounded)));

        // One worker keeps Hangfire from opening many parallel DB sessions.
        services.AddHangfireServer(options =>
        {
            options.WorkerCount = 1;
        });

        return services;
    }

    public static WebApplication UsePlatformHangfireDashboard(this WebApplication app)
    {
        app.UseHangfireDashboard(
            "/hangfire",
            new DashboardOptions
            {
                Authorization = [new HangfireDashboardAuthorizationFilter()],
                DashboardTitle = "Platform Jobs",
            });

        return app;
    }

    public static WebApplication MapPlatformRecurringJobs(this WebApplication app)
    {
        var brazilTimeZone = ResolveBrazilTimeZone();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Platform.Api.Jobs.HangfireRegistration");

        logger.LogInformation(
            "Registering Hangfire recurring jobs with time zone {TimeZoneId}.",
            brazilTimeZone.Id);

        var recurringJobOptions = new RecurringJobOptions
        {
            TimeZone = brazilTimeZone,
        };

        RecurringJob.AddOrUpdate<DataRetentionJob>(
            DataRetentionJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            "0 0 * * *",
            recurringJobOptions);

        RecurringJob.AddOrUpdate<TrialLifecycleJob>(
            TrialLifecycleJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            "0 0 * * *",
            recurringJobOptions);

        RecurringJob.AddOrUpdate<PmocEngineJob>(
            PmocEngineJobId,
            job => job.ExecuteAsync(CancellationToken.None),
            "0 6 * * *",
            recurringJobOptions);

        return app;
    }

    /// <summary>
    /// Windows uses "E. South America Standard Time"; Linux (Railway Docker) uses "America/Sao_Paulo".
    /// </summary>
    public static TimeZoneInfo ResolveBrazilTimeZone() => BrazilTimeZone.Resolve();

    public static DateOnly GetBrazilToday() => BrazilTimeZone.GetToday(TimeProvider.System);
}
