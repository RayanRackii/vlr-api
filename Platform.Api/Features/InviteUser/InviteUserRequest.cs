namespace Platform.Api.Features.InviteUser;

public sealed record InviteUserRequest(
    string Name,
    string Email,
    string Role);
