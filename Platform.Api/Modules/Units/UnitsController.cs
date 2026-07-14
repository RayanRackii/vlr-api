using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Units;

public sealed record UnitResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    string? Code,
    bool IsActive);

[ApiController]
[Authorize]
[Route("api/units")]
public sealed class UnitsController(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UnitResponse>>> List(
        CancellationToken cancellationToken)
    {
        if (tenantProvider.TenantId is null)
        {
            return Unauthorized(new { error = "Tenant context is required." });
        }

        var units = await dbContext.Units
            .AsNoTracking()
            .OrderBy(unit => unit.Name)
            .Select(unit => new UnitResponse(
                unit.Id,
                unit.TenantId,
                unit.Name,
                unit.Code,
                unit.IsActive))
            .ToListAsync(cancellationToken);

        return Ok(units);
    }
}
