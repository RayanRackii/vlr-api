using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Modules.Billing.Services;

namespace Platform.Api.Modules.Billing.Controllers;

/// <summary>Anonymous read of the active plan catalog (pricing page).</summary>
[ApiController]
[Route("api/public/plans")]
public sealed class PublicPlansController(IPlanCatalogService catalog) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<IReadOnlyList<PublicPlanDto>>> List(CancellationToken cancellationToken) =>
        Ok(await catalog.ListActiveAsync(cancellationToken));
}
