using Microsoft.EntityFrameworkCore;
using Platform.Api.Notifications;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Auth.Services;

public sealed class PasswordRecoveryService(
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
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

        // A null tenant turns the global query filter off. Do not scan every tenant.
        if (tenantProvider.TenantId is not Guid tenantId)
        {
            logger.LogInformation(
                "Password recovery requested without a tenant; no lookup was performed.");
            return;
        }

        try
        {
            var displayName = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.TenantId == tenantId && user.Email.ToLower() == normalized)
                .OrderByDescending(user => user.CreatedAt)
                .Select(user => user.FullName)
                .FirstOrDefaultAsync(cancellationToken);

            if (displayName is null)
            {
                logger.LogInformation(
                    "Password recovery requested for an email outside the current tenant.");
                return;
            }

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

            var recovery = await supabaseAuthAdminClient.GenerateRecoveryLinkAsync(
                normalized,
                redirectTo,
                cancellationToken);

            // First-party URL — avoids Supabase Site URL fallback (often localhost:3000).
            var resetUrl =
                $"{frontendBaseUrl}/reset-password?token_hash={Uri.EscapeDataString(recovery.HashedToken)}&type=recovery";

            logger.LogInformation(
                "Password recovery link prepared for Auth user (frontend={FrontendBaseUrl}).",
                frontendBaseUrl);

            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = normalized.Split('@')[0];
            }

            var htmlBody = RolvixEmailLayout.Wrap(
                displayName,
                RolvixEmailLayout.RecoveryBody(resetUrl));

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
