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
    public static TimeZoneInfo ResolveBrazilTimeZone()
    {
        string[] candidateIds =
        [
            "E. South America Standard Time",
            "America/Sao_Paulo",
        ];

        foreach (var timeZoneId in candidateIds)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException(
            "Could not resolve Brazil time zone. Tried: E. South America Standard Time, America/Sao_Paulo.");
    }

    public static DateOnly GetBrazilToday()
    {
        var brazilTimeZone = ResolveBrazilTimeZone();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, brazilTimeZone);
        return DateOnly.FromDateTime(localNow);
    }
}
