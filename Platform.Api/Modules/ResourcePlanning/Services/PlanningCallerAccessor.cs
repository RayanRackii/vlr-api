using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>
/// The authenticated caller of a planning write: the user id, whether they manage allocations and
/// whether they may close assets (<c>Unavailable</c> blocks: <c>availability.write</c> or manage).
/// </summary>
public sealed record PlanningCaller(Guid? UserId, bool CanManageAllocations, bool CanCloseAssets = false);

public interface IPlanningCallerAccessor
{
    Task<PlanningCaller> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the caller from the request principal. The service needs it for the cancel rule (the
/// creator or an <c>allocations.manage</c> holder), which the permission attribute alone cannot
/// express. A platform admin inside the tenant counts as a manager, as in the authorization handler.
/// </summary>
public sealed class PlanningCallerAccessor(
    IHttpContextAccessor httpContextAccessor,
    IRbacActorAccessor actorAccessor,
    IPermissionResolver permissionResolver) : IPlanningCallerAccessor
{
    public async Task<PlanningCaller> GetAsync(CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User
            ?? throw new UnauthorizedAccessException("An authenticated caller is required.");

        var actor = await actorAccessor.GetAsync(principal, cancellationToken);
        var canManage = actor.IsPlatformAdminInTenant
            || (actor.UserId is { } userId
                && await permissionResolver.HasPermissionAsync(
                    actor.TenantId,
                    userId,
                    Permissions.ResourcePlanning.AllocationsManage,
                    cancellationToken));

        var canClose = canManage
            || (actor.UserId is { } closerId
                && await permissionResolver.HasPermissionAsync(
                    actor.TenantId,
                    closerId,
                    Permissions.ResourcePlanning.AvailabilityWrite,
                    cancellationToken));

        return new PlanningCaller(actor.UserId, canManage, canClose);
    }
}
