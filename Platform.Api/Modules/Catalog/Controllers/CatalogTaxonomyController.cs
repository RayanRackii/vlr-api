using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Taxonomy.Controllers;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[TaxonomyErrors]
[RequireActiveModule(PlatformModules.Catalog)]
[Route("api/catalog")]
public sealed class CatalogTaxonomyController(CatalogTaxonomyOptionsService service) : ControllerBase
{
    [HttpGet("taxonomy-options")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<TaxonomyResponse>> Staff(CancellationToken ct) => Ok(await service.GetAsync(false, ct));

    [HttpGet("portal/taxonomy-options")]
    [Authorize(Policy = "Customer")]
    public async Task<ActionResult<TaxonomyResponse>> Portal(CancellationToken ct) => Ok(await service.GetAsync(true, ct));
}
