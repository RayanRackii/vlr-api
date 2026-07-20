using MediatR;
using Platform.Api.Notifications;

namespace Platform.Api.Features.InviteUser;

public sealed class DispatchWelcomeNotificationHandler(
    NotificationQueue queue,
    IConfiguration configuration) : INotificationHandler<UserInvitedEvent>
{
    public async Task Handle(UserInvitedEvent notification, CancellationToken cancellationToken)
    {
        var frontendBaseUrl = configuration["App:FrontendBaseUrl"]?.TrimEnd('/')
            ?? "http://localhost:5173";

        var inviteUrl = $"{frontendBaseUrl}/invite?token={Uri.EscapeDataString(notification.InviteToken)}";

        var htmlBody =
            $"""
            <html>
              <body>
                <p>Bem-vindo ao Rolvix.</p>
                <p>Para ativar sua conta, defina sua senha neste link:</p>
                <p><a href="{inviteUrl}">{inviteUrl}</a></p>
              </body>
            </html>
            """;

        await queue.EnqueueAsync(
            new NotificationMessage(
                Type: "Email",
                Recipient: notification.Email,
                Subject: "Convite Rolvix",
                Body: htmlBody),
            cancellationToken);
    }
}
