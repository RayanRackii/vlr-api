namespace Platform.Api.Features.CreateTenant;

public static class CreateTenantEndpoint
{
    public static IEndpointRouteBuilder MapCreateTenantEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/onboarding/tenants",
                async (
                    CreateTenantRequest request,
                    ICreateTenantHandler handler,
                    CancellationToken cancellationToken) =>
                    await handler.HandleAsync(request, cancellationToken))
            .AllowAnonymous()
            .WithName("CreateTenant")
            .WithTags("Onboarding");

        return endpoints;
    }
}
