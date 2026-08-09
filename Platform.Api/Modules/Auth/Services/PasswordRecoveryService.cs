using Microsoft.EntityFrameworkCore;
using Platform.Api.Notifications;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Auth.Services;

public sealed class PasswordRecoveryService(
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    AppDbContext dbContext,
    NotificationQueue notificationQueue,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<PasswordRecoveryService> logger) : IPasswordRecoveryService
{
    public async Task RequestAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 320 || !normalized.Contains('@'))
        {
            return;
        }

        try
        {
            var authUserId = await supabaseAuthAdminClient.FindUserIdByEmailAsync(
                normalized,
                cancellationToken);

            if (authUserId is null)
            {
                logger.LogInformation(
                    "Password recovery requested for unknown email (no Auth user).");
                return;
            }

            var frontendBaseUrl = FrontendBaseUrlResolver.Resolve(configuration, environment);
            var redirectTo = $"{frontendBaseUrl}/reset-password";

            var actionLink = await supabaseAuthAdminClient.GenerateRecoveryLinkAsync(
                normalized,
                redirectTo,
                cancellationToken);

            var displayName = await dbContext.Users
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(u => u.Email == normalized)
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = normalized.Split('@')[0];
            }

            var htmlBody = RolvixEmailLayout.Wrap(
                displayName,
                RolvixEmailLayout.RecoveryBody(actionLink));

            await notificationQueue.EnqueueAsync(
                new NotificationMessage(
                    Type: "Email",
                    Recipient: normalized,
                    Subject: "Redefinir senha — Rolvix",
                    Body: htmlBody),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Never surface existence / provider errors to the client.
            logger.LogError(ex, "Password recovery failed for a requested email.");
        }
    }
}
