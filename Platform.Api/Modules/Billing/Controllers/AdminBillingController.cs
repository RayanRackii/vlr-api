using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Modules.Billing.Services;

namespace Platform.Api.Modules.Billing.Controllers;

/// <summary>Platform-admin billing overview (MRR and subscription counts).</summary>
[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/billing")]
public sealed class AdminBillingController(IAdminBillingSummaryService summary) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<ActionResult<BillingSummaryDto>> GetSummary(CancellationToken cancellationToken) =>
        Ok(await summary.GetAsync(cancellationToken));
}
