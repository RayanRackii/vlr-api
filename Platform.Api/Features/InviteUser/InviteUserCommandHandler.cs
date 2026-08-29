using MediatR;

namespace Platform.Api.Features.InviteUser;

public sealed class InviteUserCommandHandler : IRequestHandler<InviteUserCommand, bool>
{
    public Task<bool> Handle(InviteUserCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }
}
