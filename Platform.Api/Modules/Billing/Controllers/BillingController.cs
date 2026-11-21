using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Common;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Modules.Billing.Services;
using Platform.Api.Services.Billing;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Billing.Controllers;

/// <summary>
/// The tenant's own plan and billing (staff, permission <c>core.settings.billing</c>). Provider failures answer 502
/// or 503 with a generic message: no provider body and no key ever reaches the response.
/// </summary>
[ApiController]
[Route("api/billing")]
public sealed class BillingController(
    ITenantBillingReadService read,
    ITenantBillingService billing,
    ITenantProvider tenantProvider,
    IRbacActorAccessor actorAccessor,
    IIdempotencyService? idempotency = null) : ControllerBase
{
    /// <summary>Idempotency scope of the checkout; the principal is the staff user who started it.</summary>
    public const string CheckoutIdempotencyScope = "billing.checkout";

    [HttpGet("subscription")]
    [RequirePermission(Permissions.Core.SettingsBilling)]
    public async Task<ActionResult<TenantBillingSubscriptionDto>> GetSubscription(CancellationToken cancellationToken)
    {
        if (tenantProvider.TenantId is not { } tenantId)
        {
            return Unauthorized(new { error = "Tenant context is required." });
        }

        return Ok(await read.GetSubscriptionAsync(tenantId, cancellationToken));
    }

    [HttpGet("plans")]
    [RequirePermission(Permissions.Core.SettingsBilling)]
    public async Task<ActionResult<TenantBillingPlansDto>> GetPlans(CancellationToken cancellationToken)
    {
        if (tenantProvider.TenantId is not { } tenantId)
        {
            return Unauthorized(new { error = "Tenant context is required." });
        }

        return Ok(await read.GetPlansAsync(tenantId, cancellationToken));
    }

    /// <summary>
    /// Subscribes the tenant: creates the provider customer and subscription and returns where to pay. The
    /// optional <c>Idempotency-Key</c> header makes a retry return the original answer instead of creating
    /// another subscription; the provider calls are retry-safe on their own (deterministic external reference).
    /// </summary>
    [HttpPost("subscription/checkout")]
    [RequirePermission(Permissions.Core.SettingsBilling)]
    public async Task<ActionResult<CheckoutResponseDto>> Checkout(
        [FromBody] CheckoutRequestDto request,
        CancellationToken cancellationToken,
        [FromHeader(Name = IdempotencyHeader.Name)] string? idempotencyKey = null)
    {
        if (tenantProvider.TenantId is not { } tenantId)
        {
            return Unauthorized(new { error = "Tenant context is required." });
        }

        try
        {
            var actorEmail = User.FindFirstValue("email") ?? User.FindFirstValue(ClaimTypes.Email);
            if (idempotencyKey is null)
            {
                return Ok(await billing.CheckoutAsync(tenantId, actorEmail, request, cancellationToken));
            }

            if (this.ValidateIdempotencyKey(idempotencyKey) is { } invalidKey)
            {
                return invalidKey;
            }

            var actor = await actorAccessor.GetAsync(User, cancellationToken);
            var principalId = actor.UserId ?? tenantId;
            var payload = new
            {
                request.PlanCatalogId,
                BillingCycle = request.BillingCycle?.Trim().ToLowerInvariant(),
                Method = request.Method?.Trim().ToLowerInvariant(),
            };

            return await this.CreateIdempotentAsync(
                idempotency,
                CheckoutIdempotencyScope,
                principalId,
                idempotencyKey,
                payload,
                StatusCodes.Status200OK,
                () => billing.CheckoutAsync(tenantId, actorEmail, request, cancellationToken),
                response => response.SubscriptionId,
                async subscriptionId => await billing.LoadCheckoutAsync(
                    tenantId, subscriptionId, request.Method, cancellationToken),
                location: null,
                cancellationToken);
        }
        catch (BillingException ex)
        {
            return ErrorResult(ex);
        }
    }

    /// <summary>Upgrade takes effect now; a downgrade takes effect with the next period and is refused when usage is above the new limits.</summary>
    [HttpPost("subscription/change-plan")]
    [RequirePermission(Permissions.Core.SettingsBilling)]
    public async Task<ActionResult<TenantBillingSubscriptionDto>> ChangePlan(
        [FromBody] ChangePlanRequestDto request,
        CancellationToken cancellationToken)
    {
        if (tenantProvider.TenantId is not { } tenantId)
        {
            return Unauthorized(new { error = "Tenant context is required." });
        }

        try
        {
            await billing.ChangePlanAsync(tenantId, request, cancellationToken);
            return Ok(await read.GetSubscriptionAsync(tenantId, cancellationToken));
        }
        catch (BillingException ex)
        {
            return ErrorResult(ex);
        }
    }

    /// <summary>Cancels at the end of the paid period. Data is kept; the workspace becomes read-only afterwards.</summary>
    [HttpPost("subscription/cancel")]
    [RequirePermission(Permissions.Core.SettingsBilling)]
    public async Task<ActionResult<TenantBillingSubscriptionDto>> Cancel(CancellationToken cancellationToken)
    {
        if (tenantProvider.TenantId is not { } tenantId)
        {
            return Unauthorized(new { error = "Tenant context is required." });
        }

        try
        {
            await billing.CancelAsync(tenantId, cancellationToken);
            return Ok(await read.GetSubscriptionAsync(tenantId, cancellationToken));
        }
        catch (BillingException ex)
        {
            return ErrorResult(ex);
        }
    }

    private ObjectResult ErrorResult(BillingException exception)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["error"] = exception.Message,
            ["code"] = exception.Code,
        };
        foreach (var (key, value) in exception.Details)
        {
            body[key] = value;
        }

        return StatusCode(exception.HttpStatus, body);
    }
}
