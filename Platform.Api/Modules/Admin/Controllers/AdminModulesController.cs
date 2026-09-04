using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Admin.Controllers;

[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/modules")]
public sealed class AdminModulesController : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<AdminModuleCatalogItemDto>> List()
    {
        var items = PlatformModuleCatalog.Commercial
            .Select(module => new AdminModuleCatalogItemDto(
                module.Key,
                module.IsCommercial,
                module.IsLegacy,
                module.Provides,
                module.RequiredCapabilities,
                module.Aliases))
            .ToList();

        return Ok(items);
    }
}
