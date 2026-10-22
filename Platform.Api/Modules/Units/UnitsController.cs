using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Units;

public sealed record UnitResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    string? Code,
    bool IsActive);

public sealed record CreateUnitRequest
{
    public required string Name { get; init; }

    public string? Code { get; init; }
}

[ApiController]
[Route("api/units")]
public sealed class UnitsController(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Core.UnitsRead)]
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

    [HttpPost]
    [RequirePermission(Permissions.Core.UnitsWrite)]
    public async Task<ActionResult<UnitResponse>> Create(
        [FromBody] CreateUnitRequest request,
        CancellationToken cancellationToken)
    {
        if (tenantProvider.TenantId is not { } tenantId || tenantId == Guid.Empty)
        {
            return Unauthorized(new { error = "Tenant context is required." });
        }

        var name = request.Name?.Trim();
        var code = string.IsNullOrWhiteSpace(request.Code) ? null : request.Code.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || code?.Length > 50)
        {
            return BadRequest(new { error = "Unit name is required and must be at most 200 characters; code may be at most 50 characters." });
        }

        if (await dbContext.Units.AnyAsync(unit => unit.TenantId == tenantId && (unit.Name == name || (code != null && unit.Code == code)), cancellationToken))
        {
            return Conflict(new { error = "A unit with this name or code already exists." });
        }

        var unit = new Platform.Core.Domain.Entities.Unit(tenantId, name, code);
        dbContext.Units.Add(unit);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            return Conflict(new { error = "A unit with this name or code already exists." });
        }

        var response = new UnitResponse(unit.Id, unit.TenantId, unit.Name, unit.Code, unit.IsActive);
        return CreatedAtAction(nameof(List), response);
    }
}
