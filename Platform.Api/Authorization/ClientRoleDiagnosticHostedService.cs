using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authorization;

/// <summary>
/// DEV-only diagnostic: logs how many active Client UserRoles exist. Does not delete data.
/// </summary>
public sealed class ClientRoleDiagnosticHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<ClientRoleDiagnosticHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var clientAssignments = await dbContext.UserRoles
            .IgnoreQueryFilters()
            .Include(userRole => userRole.Role)
            .Include(userRole => userRole.User)
            .Where(userRole =>
                userRole.Role.Name.ToLower() == SystemRoles.Client.ToLower()
                && userRole.User.IsActive)
            .Select(userRole => new { userRole.UserId, userRole.User.Email })
            .ToListAsync(cancellationToken);

        var count = clientAssignments.Count;
        logger.LogInformation(
            "RBAC Client role diagnostic. ActiveClientUserRoles={Count}",
            count);

        if (count == 0)
        {
            return;
        }

        var clientOnlyUserIds = await dbContext.Users
            .IgnoreQueryFilters()
            .Where(user =>
                user.IsActive
                && user.UserRoles.Any(userRole =>
                    userRole.Role.Name.ToLower() == SystemRoles.Client.ToLower())
                && user.UserRoles.All(userRole =>
                    userRole.Role.Name.ToLower() == SystemRoles.Client.ToLower()))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        if (clientOnlyUserIds.Count > 0)
        {
            logger.LogWarning(
                "USER_DECISION_REQUIRED: {LockoutCount} active users have only the Client role and would lose B2B access under permission enforcement. ActiveClientUserRoles={Count}. No data was deleted.",
                clientOnlyUserIds.Count,
                count);
            return;
        }

        logger.LogWarning(
            "USER_DECISION_REQUIRED: {Count} active Client UserRole assignments remain (isolated leftover). No data was deleted; enforcement continues.",
            count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
