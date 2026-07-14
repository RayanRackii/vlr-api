using Hangfire;
using Hangfire.PostgreSql;

namespace Platform.Api.Jobs;

public static class HangfireExtensions
{
    public const string DataRetentionJobId = "data-retention-gc";

    public const string PmocEngineJobId = "pmoc-engine";

    public static IServiceCollection AddPlatformHangfire(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                options => options.UseNpgsqlConnection(connectionString)));

        services.AddHangfireServer();

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
        var recurringJobOptions = new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc,
        };

        RecurringJob.AddOrUpdate<DataRetentionJob>(
            DataRetentionJobId,
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
}
