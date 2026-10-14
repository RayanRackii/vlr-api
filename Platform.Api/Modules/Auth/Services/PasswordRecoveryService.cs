using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
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
    IPhoneVerificationSendGate sendGate,
    ILogger<PasswordRecoveryService> logger,
    IHttpContextAccessor? httpContextAccessor = null) : IPasswordRecoveryService
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

        var clientIp = GetClientIp();
        var decision = sendGate.Decide(
            tenantId,
            normalized,
            clientIp,
            IPhoneVerificationSendGate.StaffPasswordRecoveryScope);

        if (decision == PhoneVerificationSendDecision.Limited)
        {
            throw new PhoneVerificationRateLimitedException(
                TwilioVerifyPhoneVerificationClient.RateLimitedMessage);
        }

        if (decision == PhoneVerificationSendDecision.Cooldown)
        {
            return;
        }

        try
        {
            var displayName = await dbContext.Users
                .AsNoTracking()
                .Where(user =>
                    user.TenantId == tenantId
                    && user.Email == normalized
                    && user.IsActive)
                .OrderByDescending(user => user.CreatedAt)
                .Select(user => user.FullName)
                .FirstOrDefaultAsync(cancellationToken);

            if (displayName is null)
            {
                logger.LogInformation(
                    "Password recovery requested for an email without an active membership in the current tenant.");
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

            sendGate.RecordSuccess(
                tenantId,
                normalized,
                clientIp,
                IPhoneVerificationSendGate.StaffPasswordRecoveryScope);
        }
        catch (Exception ex)
        {
            // Never surface existence / provider errors to the client.
            logger.LogError(ex, "Password recovery failed for a requested email.");
        }
    }

    private string? GetClientIp()
    {
        var httpContext = httpContextAccessor?.HttpContext;
        if (httpContext is null)
        {
            return null;
        }

        var forwarded = httpContext.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var firstHop = forwarded.Split(',', 2)[0].Trim();
            if (firstHop.Length > 0)
            {
                return firstHop;
            }
        }

        return httpContext.Connection.RemoteIpAddress?.ToString();
    }
}
