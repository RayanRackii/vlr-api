using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

/// <summary>
/// Resource-centric agenda (rentals.schedule.read). Lives in the Rentals namespace so the module
/// gate convention matches its permission; allocations come from Catalog orders (resource-planning),
/// so the Rentals module gate applies here.
/// </summary>
[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/catalog/agenda")]
public sealed class ResourceAgendaController(
    ICatalogAgendaService agendaService,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("resources")]
    [RequirePermission(Permissions.Rentals.ScheduleRead)]
    public async Task<ActionResult<CatalogAgendaResourcesResponse>> Resources(
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] Guid? rentalAssetId,
        CancellationToken cancellationToken)
    {
        if (CatalogAgendaController.ValidateRange(from, to) is { } error)
        {
            return error;
        }

        // Equipment-centric callers (rentals.schedule.read) see the schedule, but customer name
        // and order display number are commercial data gated behind catalog.orders.read.
        var canReadOrders = await authorizationService.AuthorizeAsync(
            User, PermissionPolicies.Name(Permissions.Catalog.OrdersRead));

        var response = await agendaService.GetResourcesAgendaAsync(
            new CatalogAgendaQuery(from, to, ProductId: null, rentalAssetId),
            includeOrderDetails: canReadOrders.Succeeded,
            cancellationToken);
        return Ok(response);
    }

}
