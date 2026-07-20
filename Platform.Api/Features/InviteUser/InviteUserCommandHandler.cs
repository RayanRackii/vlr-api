using System.Net.Mail;
using MediatR;

namespace Platform.Api.Features.InviteUser;

public sealed class InviteUserCommandHandler(
    IMediator mediator,
    ILogger<InviteUserCommandHandler> logger) : IRequestHandler<InviteUserCommand, bool>
{
    public async Task<bool> Handle(InviteUserCommand request, CancellationToken cancellationToken)
    {
        if (!IsValid(request))
        {
            return false;
        }

        // Simulated EF Core persistence until the invite token table is modeled.
        var inviteToken = Guid.NewGuid().ToString("N");

        logger.LogInformation(
            "Simulated user invite persisted. Name={Name}, Email={Email}, Role={Role}, InviteToken={InviteToken}",
            request.Name.Trim(),
            request.Email.Trim(),
            request.Role.Trim(),
            inviteToken);

        await mediator.Publish(
            new UserInvitedEvent(request.Email.Trim(), inviteToken),
            cancellationToken);

        return true;
    }

    private static bool IsValid(InviteUserCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Role))
        {
            return false;
        }

        try
        {
            _ = new MailAddress(request.Email.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
