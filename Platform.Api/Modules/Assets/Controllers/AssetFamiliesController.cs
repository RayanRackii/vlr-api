using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Assets.Controllers;

[ApiController]
[Route("api/asset-families")]
[Authorize]
public sealed class AssetFamiliesController(
    IAssetFamilyService assetFamilyService,
    ITenantProvider tenantProvider) : ControllerBase
{
    /// <summary>Platform catalog of active asset families (for onboarding multi-select).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AssetFamilyDetailResponse>>> ListCatalog(
        CancellationToken cancellationToken)
    {
        var families = await assetFamilyService.ListCatalogAsync(cancellationToken);
        return Ok(families);
    }

    /// <summary>Families enabled for the current tenant (forms + copy).</summary>
    [HttpGet("active")]
    public async Task<ActionResult<IReadOnlyList<AssetFamilyDetailResponse>>> ListActive(
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var families = await assetFamilyService.ListActiveForTenantAsync(
            tenantId,
            cancellationToken);

        return Ok(families);
    }
}
