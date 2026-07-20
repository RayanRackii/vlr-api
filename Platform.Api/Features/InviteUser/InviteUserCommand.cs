using MediatR;

namespace Platform.Api.Features.InviteUser;

public sealed record InviteUserCommand(
    string Name,
    string Email,
    string Role) : IRequest<bool>;
