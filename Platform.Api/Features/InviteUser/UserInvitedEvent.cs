using MediatR;

namespace Platform.Api.Features.InviteUser;

public sealed record UserInvitedEvent(
    string Email,
    string InviteToken) : INotification;
