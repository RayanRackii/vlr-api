using Platform.Api.Modules.WorkOrders.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class PmocEngineJob(
    AppDbContext dbContext,
    IWorkOrderGenerationService workOrderGenerationService,
    ILogger<PmocEngineJob> logger)
{
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = dbContext;
        _ = workOrderGenerationService;

        var today = HangfireExtensions.GetBrazilToday();
        logger.LogInformation(
            "PmocEngineJob is fail-closed until Phase 3 Slice 3 interval generation. Skipping all plans for {ScheduledDate}.",
            today);

        return Task.CompletedTask;
    }
}
