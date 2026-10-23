using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.ResourceVisuals;

namespace Platform.Api.Modules.ResourceVisuals;

[ApiController]
[ResourceVisualErrors]
[Route("api/admin/resource-visual-templates")]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
public sealed class AdminResourceVisualTemplatesController(ResourceVisualCatalogService catalog) : ControllerBase
{
    [HttpGet, Authorize(Policy = ResourceVisualPermissions.TemplatesRead)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] bool includeArchived = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default) => Ok(await catalog.ListAsync(new(search, null, null, Page: page, PageSize: pageSize), false, includeArchived, ct));
    [HttpGet("{id:guid}"), Authorize(Policy = ResourceVisualPermissions.TemplatesRead)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(await catalog.GetAsync(id, ct));
    [HttpPost, Authorize(Policy = ResourceVisualPermissions.TemplatesManage)]
    public async Task<IActionResult> Create(TemplateRequest request, CancellationToken ct)
    {
        var result = await catalog.SaveTemplateAsync(null, request, ct); return CreatedAtAction(nameof(Get), new { id = result.Id }, result);
    }
    [HttpPut("{id:guid}"), Authorize(Policy = ResourceVisualPermissions.TemplatesManage)]
    public async Task<IActionResult> Update(Guid id, TemplateRequest request, CancellationToken ct) => Ok(await catalog.SaveTemplateAsync(id, request, ct));
    [HttpDelete("{id:guid}"), Authorize(Policy = ResourceVisualPermissions.TemplatesManage)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) { await catalog.ArchiveAsync(id, ct); return NoContent(); }
    [HttpGet("{id:guid}/revisions"), Authorize(Policy = ResourceVisualPermissions.TemplatesRead)]
    public async Task<IActionResult> Revisions(Guid id, CancellationToken ct) => Ok((await catalog.RevisionsAsync(id, ct)).Select(AdminVisualRevisionDto.From));
    [HttpPost("{id:guid}/revisions"), Authorize(Policy = ResourceVisualPermissions.TemplatesManage)]
    public async Task<IActionResult> Draft(Guid id, RevisionRequest request, CancellationToken ct) => Ok(AdminVisualRevisionDto.From(await catalog.SaveRevisionAsync(id, null, request, ct)));
    [HttpPut("{id:guid}/revisions/{revisionId:guid}"), Authorize(Policy = ResourceVisualPermissions.TemplatesManage)]
    public async Task<IActionResult> UpdateDraft(Guid id, Guid revisionId, RevisionRequest request, CancellationToken ct) => Ok(AdminVisualRevisionDto.From(await catalog.SaveRevisionAsync(id, revisionId, request, ct)));
    [HttpPost("{id:guid}/revisions/{revisionId:guid}/publish"), Authorize(Policy = ResourceVisualPermissions.TemplatesManage)]
    public async Task<IActionResult> Publish(Guid id, Guid revisionId, CancellationToken ct) => Ok(await catalog.PublishAsync(id, revisionId, ct));
}

[ApiController]
[ResourceVisualErrors]
[Route("api/admin/resource-visual-tags")]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Authorize(Policy = ResourceVisualPermissions.TagsManage)]
public sealed class AdminResourceVisualTagsController(ResourceVisualCatalogService catalog) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await catalog.TagsAsync(false, ct));
    [HttpPost] public async Task<IActionResult> Create(TagRequest request, CancellationToken ct) => Ok(await catalog.SaveTagAsync(null, request, ct));
    [HttpPut("{id:guid}")] public async Task<IActionResult> Update(Guid id, TagRequest request, CancellationToken ct) => Ok(await catalog.SaveTagAsync(id, request, ct));
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await catalog.DeleteTagAsync(id, ct); return NoContent(); }
}

[ApiController]
[ResourceVisualErrors]
[Route("api/admin/tenants/{tenantId:guid}/resource-segments")]
[Authorize(Policy = SupabaseAuthenticationExtensions.PlatformAdminPolicy)]
[Authorize(Policy = ResourceVisualPermissions.TenantSegmentsManage)]
public sealed class AdminTenantResourceSegmentsController(ResourceVisualCatalogService catalog) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> Get(Guid tenantId, CancellationToken ct) => Ok(await catalog.SegmentsAsync(tenantId, ct));
    [HttpPut] public async Task<IActionResult> Replace(Guid tenantId, SegmentsRequest request, CancellationToken ct) => Ok(await catalog.ReplaceSegmentsAsync(tenantId, request, ct));
}

[ApiController]
[ResourceVisualErrors]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/rental-assets")]
public sealed class ResourceVisualPickerController(ResourceVisualCatalogService catalog) : ControllerBase
{
    [HttpGet("visual-templates"), RequireAnyPermission(Permissions.Rentals.AssetsRead, Permissions.Rentals.LayoutsRead)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery(Name = "segmentIds")] Guid[]? segmentIds, [FromQuery(Name = "tagIds")] Guid[]? tagIds, [FromQuery] bool allSegments = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default) => Ok(await catalog.ListAsync(new(search, CombineIds(segmentIds, "segmentIds[]"), CombineIds(tagIds, "tagIds[]"), allSegments, page, pageSize), true, false, ct));
    private Guid[] CombineIds(Guid[]? bound, string bracketKey)
    {
        var result = (bound ?? []).ToList();
        foreach (var value in Request.Query[bracketKey])
        {
            if (!Guid.TryParse(value, out var id)) throw new ResourceVisualException("SEGMENT_INVALID", "Tag filters must be UUIDs.");
            result.Add(id);
        }
        return result.Distinct().ToArray();
    }
    [HttpGet("visual-template-tags"), RequireAnyPermission(Permissions.Rentals.AssetsRead, Permissions.Rentals.LayoutsRead)]
    public async Task<IActionResult> Tags(CancellationToken ct) => Ok(await catalog.TagsAsync(true, ct));
}

[ApiController]
[ResourceVisualErrors]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/rentals/resource-segments")]
public sealed class TenantResourceSegmentsController(ResourceVisualCatalogService catalog) : ControllerBase
{
    [HttpGet, RequirePermission(ResourceVisualPermissions.SegmentsRead)] public async Task<IActionResult> Get(CancellationToken ct) => Ok(await catalog.SegmentsAsync(null, ct));
    [HttpPut, RequirePermission(ResourceVisualPermissions.SegmentsWrite)] public async Task<IActionResult> Replace(SegmentsRequest request, CancellationToken ct) => Ok(await catalog.ReplaceSegmentsAsync(null, request, ct));
}

[ApiController]
[ResourceVisualErrors]
[Route("api/public/resource-visual-revisions")]
public sealed class PublicResourceVisualArtController(ResourceVisualCatalogService catalog) : ControllerBase
{
    [HttpGet("{id:guid}/art.svg"), AllowAnonymous]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var revision = await catalog.PublishedArtAsync(id, ct);
        if (revision == null) return NotFound(new { code = "VISUAL_TEMPLATE_INVALID", error = "Published visual revision not found." });
        var etag = $"\"{revision.ArtSha256}\"";
        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'none'; sandbox";
        Response.Headers.XContentTypeOptions = "nosniff";
        if (Request.Headers.IfNoneMatch.Any(value => value?.Split(',').Any(candidate => candidate.Trim() == etag || candidate.Trim() == "*" || candidate.Trim() == "W/" + etag) == true)) return StatusCode(304);
        return Content(ResourceSvgSanitizer.Compose(revision.ViewBox, revision.FloorSvg, revision.LinesSvg, revision.NetSvg), "image/svg+xml", System.Text.Encoding.UTF8);
    }
}
