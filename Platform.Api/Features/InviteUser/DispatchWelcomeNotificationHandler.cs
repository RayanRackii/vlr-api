using MediatR;
using Platform.Api.Notifications;

namespace Platform.Api.Features.InviteUser;

public sealed class DispatchWelcomeNotificationHandler(
    NotificationQueue queue,
    IConfiguration configuration,
    IHostEnvironment environment) : INotificationHandler<UserInvitedEvent>
{
    public async Task Handle(UserInvitedEvent notification, CancellationToken cancellationToken)
    {
        var frontendBaseUrl = FrontendBaseUrlResolver.Resolve(configuration, environment);
        var inviteUrl = $"{frontendBaseUrl}/invite?token={Uri.EscapeDataString(notification.InviteToken)}";

        var htmlBody = RolvixEmailLayout.Wrap(
            notification.Email,
            RolvixEmailLayout.InviteBody(inviteUrl));

        await queue.EnqueueAsync(
            new NotificationMessage(
                Type: "Email",
                Recipient: notification.Email,
                Subject: "Convite Rolvix — defina sua senha",
                Body: htmlBody),
            cancellationToken);
    }
}
