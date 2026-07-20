using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Platform.Api.Features.InviteUser;

public static class InviteUserEndpoint
{
    public static IEndpointRouteBuilder MapInviteUserEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/users/invite",
                async (
                    [FromBody] InviteUserRequest request,
                    ISender mediator,
                    CancellationToken cancellationToken) =>
                {
                    var succeeded = await mediator.Send(
                        new InviteUserCommand(request.Name, request.Email, request.Role),
                        cancellationToken);

                    return succeeded
                        ? Results.Ok(new { message = "Invite queued successfully." })
                        : Results.BadRequest(new { error = "Invalid invite payload." });
                })
            .RequireAuthorization()
            .WithName("InviteUser")
            .WithTags("Users");

        return endpoints;
    }
}
