using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

using Platform.Api.Modules.Rentals.Controllers;

namespace Platform.Api.Modules.Admin.Controllers;

[ApiController]
[BookingMapModelValidation]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Authorize(Policy = ResourceVisualPermissions.DemoMapsSeed)]
[Route("api/admin/tenants/{tenantId:guid}/booking-maps/demo")]
public sealed class AdminBookingMapDemoController(IRentalLayoutService maps, AmbientTenantContext ambient,
    IHostEnvironment environment) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> SeedDemo(Guid tenantId, SeedDemoBookingMapRequest request, CancellationToken ct)
    {
        if (!environment.IsDevelopment()) return NotFound(new { code = "DEV_ONLY", error = "This operation is available only in Development." });
        if (tenantId == Guid.Empty) return BadRequest(new { code = "MAP_INVALID", error = "Tenant id is required." });
        var previous = ambient.TenantId;
        try
        {
            ambient.TenantId = tenantId;
            return Ok(await maps.SeedDemoAsync(request, ct));
        }
        catch (BookingMapException ex) { return StatusCode(ex.StatusCode, new { code = ex.Code, error = ex.Message }); }
        finally { ambient.TenantId = previous; }
    }
}
