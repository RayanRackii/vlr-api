using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authentication;

/// <summary>
/// For Platform Super-Admins only: binds <c>X-Support-Tenant-Id</c> into
/// <see cref="AmbientTenantContext"/> so product APIs run in that tenant scope.
/// </summary>
public sealed class SupportTenantMiddleware(
    RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        AmbientTenantContext ambientTenantContext,
        IPlatformAdminChecker platformAdminChecker,
        AppDbContext dbContext)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && platformAdminChecker.IsPlatformAdmin(context.User)
            && context.Request.Headers.TryGetValue(TenantHeaders.SupportTenantId, out var values))
        {
            var raw = values.FirstOrDefault()?.Trim();

            if (!string.IsNullOrWhiteSpace(raw))
            {
                if (!Guid.TryParse(raw, out var tenantId))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "X-Support-Tenant-Id must be a valid GUID.",
                    });
                    return;
                }

                var tenantIsActive = await dbContext.Tenants
                    .AsNoTracking()
                    .AnyAsync(
                        t => t.Id == tenantId && t.IsActive,
                        context.RequestAborted);

                if (!tenantIsActive)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Support tenant was not found or is inactive.",
                    });
                    return;
                }

                ambientTenantContext.TenantId = tenantId;
            }
        }

        await next(context);
    }
}
