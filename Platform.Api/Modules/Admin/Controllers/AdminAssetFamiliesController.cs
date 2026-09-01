using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;

namespace Platform.Api.Modules.Admin.Controllers;

[ApiController]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Route("api/admin/asset-families")]
public sealed class AdminAssetFamiliesController(IAssetFamilyService assetFamilyService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AssetFamilyDetailResponse>>> ListCatalog(
        CancellationToken cancellationToken)
    {
        var families = await assetFamilyService.ListCatalogAsync(cancellationToken);
        return Ok(families);
    }
}
