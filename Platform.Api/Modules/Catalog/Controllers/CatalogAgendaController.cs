using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Catalog.Controllers;

/// <summary>
/// Read-only staff agenda over the Phase 0 order ↔ resource planning data. Every query is tenant
/// scoped through the ambient global filters (no <c>IgnoreQueryFilters</c>) and bounded to a
/// 42-day window so a single read can never sweep an unbounded range.
/// </summary>
[ApiController]
[RequireActiveModule(PlatformModules.Catalog)]
[Route("api/catalog/agenda")]
public sealed class CatalogAgendaController(
    ICatalogAgendaService agendaService) : ControllerBase
{
    internal const int MaxRangeDays = 42;

    [HttpGet("products")]
    [RequirePermission(Permissions.Catalog.OrdersRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogAgendaItemResponse>>> Products(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] Guid? productId,
        CancellationToken cancellationToken)
    {
        if (ValidateRange(from, to) is { } error)
        {
            return error;
        }

        var items = await agendaService.GetProductsAgendaAsync(
            new CatalogAgendaQuery(from, to, productId, RentalAssetId: null),
            cancellationToken);
        return Ok(items);
    }

    internal static BadRequestObjectResult? ValidateRange(DateTimeOffset from, DateTimeOffset to)
    {
        // Buffers pad the SQL window by a day each side; reject bounds that would overflow.
        if (from.UtcDateTime.Year < 2000 || to.UtcDateTime.Year > 2999)
        {
            return new BadRequestObjectResult(new { error = "'from' and 'to' must be between years 2000 and 2999." });
        }

        if (from >= to)
        {
            return new BadRequestObjectResult(new { error = "'from' must be strictly before 'to'." });
        }

        if (to - from > TimeSpan.FromDays(MaxRangeDays))
        {
            return new BadRequestObjectResult(new { error = $"The requested range must not exceed {MaxRangeDays} days." });
        }

        return null;
    }
}
