using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Catalog)]
[Route("api/catalog/policy")]
public sealed class CatalogPolicyController(
    CatalogPolicyService policyService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogPolicyResponse>> Get(CancellationToken cancellationToken)
    {
        try { return Ok(await policyService.GetAsync(cancellationToken)); }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { error = ex.Message }); }
    }

    [HttpPut]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CatalogPolicyResponse>> Put(
        [FromBody] UpsertCatalogPolicyRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await policyService.UpsertAsync(request, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { error = ex.Message }); }
    }

    [HttpGet("~/api/catalog/portal/policy")]
    [Authorize(Policy = "Customer")]
    public async Task<ActionResult<PortalCatalogPolicyResponse>> GetPortal(CancellationToken cancellationToken)
    {
        try
        {
            var policy = await policyService.GetAsync(cancellationToken);
            return Ok(new PortalCatalogPolicyResponse(policy.CustomerCanCancelOrders,
                policy.CustomerCanCancelRequests,
                new(policy.OrderCancelableUpToStatus, policy.RequestCancelableUpToStatus),
                policy.CancellationPolicyText));
        }
        catch (UnauthorizedAccessException ex) { return Unauthorized(new { error = ex.Message }); }
    }
}
