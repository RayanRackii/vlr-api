using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Services.Trial;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.ResourceVisuals;

namespace Platform.Api.Modules.ResourceVisuals;

public sealed partial class ResourceVisualCatalogService(AppDbContext db, ITenantProvider tenantProvider, ITrialGuard trialGuard, ILogger<ResourceVisualCatalogService> logger)
{
    private static ResourceVisualException Invalid(string message) => new("VISUAL_TEMPLATE_INVALID", message);
    private static ResourceVisualException Missing() => new("VISUAL_TEMPLATE_INVALID", "Visual template or revision not found.", 404);
    private Guid Tenant() => tenantProvider.TenantId is { } id && id != Guid.Empty ? id : throw new ResourceVisualException("TENANT_REQUIRED", "Tenant context is required.", 403);

    public async Task<VisualPage> ListAsync(VisualPickerQuery query, bool picker, bool includeArchived, CancellationToken ct)
    {
        var tenant = picker ? Tenant() : (Guid?)null;
        if (query.Page < 1 || query.PageSize is < 1 or > 100 || query.Search?.Length > 200 || query.SegmentIds?.Length > 100 || query.TagIds?.Length > 100) throw Invalid("Invalid catalog pagination or filter.");
        var tags = await db.Set<ResourceVisualTag>().AsNoTracking().ToListAsync(ct);
        var links = await db.Set<ResourceVisualTemplateTag>().AsNoTracking().ToListAsync(ct);
        var templates = await db.Set<ResourceVisualTemplate>().AsNoTracking().Where(x => includeArchived || x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Slug).ToListAsync(ct);
        var templateIds = templates.Select(x => x.Id).ToArray();
        var published = await db.Set<ResourceVisualTemplateRevision>().AsNoTracking()
            .Where(x => x.Status == ResourceVisualRevisionStatus.Published && templateIds.Contains(x.TemplateId))
            .Where(x => !db.Set<ResourceVisualTemplateRevision>().Any(later => later.TemplateId == x.TemplateId && later.Status == ResourceVisualRevisionStatus.Published && later.RevisionNo > x.RevisionNo))
            .Select(x => new RevisionMetadata(x.Id, x.TemplateId, x.RevisionNo, x.Status, x.ViewBox, x.AspectRatio, x.DefaultColor, x.DefaultIcon, x.SuggestedSlotMinutes, x.SuggestedCapacityPeople, x.DefaultSchedulePolicy, x.DefaultOpenTime, x.DefaultCloseTime, x.DefaultAllowedDurationMinutes, x.DefaultPricings, x.DefaultRequiresDeposit, x.DefaultMaxAdvanceBookingDays, x.ArtSha256, x.Version)).ToArrayAsync(ct);
        var segments = (query.SegmentIds ?? []).Distinct().ToArray();
        if (picker && segments.Length == 0 && !query.AllSegments)
            segments = await db.Set<TenantResourceSegment>().Where(x => x.TenantId == tenant!.Value && db.Set<ResourceVisualTag>().Any(t => t.Id == x.TagId && t.IsActive)).Select(x => x.TagId).ToArrayAsync(ct);
        var requested = (query.TagIds ?? []).Concat(segments).Distinct().ToArray();
        var selectedTags = tags.Where(x => requested.Contains(x.Id) && x.IsActive).ToArray();
        if (selectedTags.Length != requested.Length || selectedTags.Any(x => segments.Contains(x.Id) && x.Kind != ResourceVisualTagKind.Segment)) throw new ResourceVisualException("SEGMENT_INVALID", "Filters must reference active catalog tags and segments.");
        var groups = selectedTags.GroupBy(x => x.Facet).Select(x => x.Select(t => t.Id).ToHashSet()).ToArray();
        var words = (query.Search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<VisualTemplateDto>();
        foreach (var template in templates)
        {
            var ownTags = tags.Where(t => links.Any(l => l.TemplateId == template.Id && l.TagId == t.Id)).ToArray();
            if (groups.Any(group => !ownTags.Any(t => group.Contains(t.Id)))) continue;
            var text = string.Join(" ", template.NameI18n.Values.Concat(template.DescriptionI18n.Values).Concat(ownTags.SelectMany(t => t.NameI18n.Values)).Append(template.Slug));
            if (words.Any(word => !text.Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            var revision = published.Where(x => x.TemplateId == template.Id).MaxBy(x => x.RevisionNo);
            if (picker && revision == null) continue;
            result.Add(new(template.Id, template.Slug, template.NameI18n, template.DescriptionI18n, template.IsActive, template.SortOrder, template.Version, ownTags.Select(TagDto).ToArray(), revision == null ? null : revision.ToDto()));
        }
        return new(result.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArray(), result.Count, query.Page, query.PageSize);
    }

    public async Task<VisualTemplateDto> GetAsync(Guid id, CancellationToken ct)
    {
        var template = await db.Set<ResourceVisualTemplate>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing();
        var links = await db.Set<ResourceVisualTemplateTag>().Where(x => x.TemplateId == id).Select(x => x.TagId).ToArrayAsync(ct);
        var tags = await db.Set<ResourceVisualTag>().AsNoTracking().Where(x => links.Contains(x.Id)).ToArrayAsync(ct);
        var revision = await db.Set<ResourceVisualTemplateRevision>().AsNoTracking().Where(x => x.TemplateId == id && x.Status == ResourceVisualRevisionStatus.Published).OrderByDescending(x => x.RevisionNo).FirstOrDefaultAsync(ct);
        return ToDto(template, tags, revision);
    }

    public async Task<VisualTemplateDto> SaveTemplateAsync(Guid? id, TemplateRequest request, CancellationToken ct)
    {
        ValidateSlug(request.Slug); ValidateTranslations(request.NameI18n, 200, true); ValidateTranslations(request.DescriptionI18n, 2000, false);
        if (request.TagIds == null || request.TagIds.Length > 100) throw Invalid("At most 100 tags are allowed.");
        var tagIds = request.TagIds.Distinct().ToArray();
        if (await db.Set<ResourceVisualTag>().CountAsync(x => tagIds.Contains(x.Id) && x.IsActive, ct) != tagIds.Length) throw Invalid("Template tags must be active global tags.");
        var entity = id == null ? new ResourceVisualTemplate { Slug = request.Slug } : await db.Set<ResourceVisualTemplate>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing();
        if (entity.Slug != request.Slug) throw Invalid("A template slug is immutable.");
        CheckVersion(request.ExpectedVersion, entity.Version);
        if (await db.Set<ResourceVisualTemplate>().AnyAsync(x => x.Slug == request.Slug && x.Id != entity.Id, ct)) throw Invalid("Template slug is already used.");
        entity.NameI18n = new(request.NameI18n); entity.DescriptionI18n = new(request.DescriptionI18n); entity.IsActive = request.IsActive; entity.SortOrder = request.SortOrder;
        if (id == null) db.Add(entity); else { entity.Version++; entity.Touch(); }
        var existing = await db.Set<ResourceVisualTemplateTag>().Where(x => x.TemplateId == entity.Id).ToListAsync(ct);
        db.RemoveRange(existing.Where(x => !tagIds.Contains(x.TagId)));
        db.AddRange(tagIds.Where(tag => existing.All(x => x.TagId != tag)).Select(tag => new ResourceVisualTemplateTag { TemplateId = entity.Id, TagId = tag }));
        await SaveAsync(ct); Audit("template.saved", entity.Id); return await GetAsync(entity.Id, ct);
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.Set<ResourceVisualTemplate>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing();
        entity.IsActive = false; entity.Version++; entity.Touch(); await SaveAsync(ct); Audit("template.archived", id);
    }

    public async Task<IReadOnlyList<ResourceVisualTemplateRevision>> RevisionsAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Set<ResourceVisualTemplate>().AnyAsync(x => x.Id == id, ct)) throw Missing();
        return await db.Set<ResourceVisualTemplateRevision>().AsNoTracking().Where(x => x.TemplateId == id).OrderByDescending(x => x.RevisionNo).ToArrayAsync(ct);
    }

    public async Task<ResourceVisualTemplateRevision> SaveRevisionAsync(Guid templateId, Guid? revisionId, RevisionRequest request, CancellationToken ct)
    {
        var template = await db.Set<ResourceVisualTemplate>().SingleOrDefaultAsync(x => x.Id == templateId, ct) ?? throw Missing();
        if (!template.IsActive) throw new ResourceVisualException("VISUAL_TEMPLATE_ARCHIVED", "Archived templates cannot receive revisions.");
        ValidateRevision(request);
        ResourceVisualTemplateRevision revision;
        if (revisionId == null)
        {
            if (await db.Set<ResourceVisualTemplateRevision>().AnyAsync(x => x.TemplateId == templateId && x.Status == ResourceVisualRevisionStatus.Draft, ct)) throw new ResourceVisualException("VISUAL_TEMPLATE_INVALID", "Template already has a draft.", 409);
            var max = await db.Set<ResourceVisualTemplateRevision>().Where(x => x.TemplateId == templateId).Select(x => (int?)x.RevisionNo).MaxAsync(ct) ?? 0;
            revision = new() { TemplateId = templateId, RevisionNo = max + 1 }; db.Add(revision);
        }
        else revision = await db.Set<ResourceVisualTemplateRevision>().SingleOrDefaultAsync(x => x.Id == revisionId && x.TemplateId == templateId, ct) ?? throw Missing();
        if (revision.Status == ResourceVisualRevisionStatus.Published) throw new ResourceVisualException("VISUAL_TEMPLATE_INVALID", "Published revisions are immutable.", 409);
        CheckVersion(request.ExpectedVersion, revision.Version);
        revision.FloorSvg = request.FloorSvg; revision.LinesSvg = request.LinesSvg; revision.NetSvg = request.NetSvg; revision.ViewBox = request.ViewBox; revision.AspectRatio = request.AspectRatio;
        revision.DefaultColor = request.DefaultColor.ToUpperInvariant(); revision.DefaultIcon = request.DefaultIcon; revision.SuggestedSlotMinutes = request.SuggestedSlotMinutes; revision.SuggestedCapacityPeople = request.SuggestedCapacityPeople;
        revision.DefaultSchedulePolicy = request.DefaultSchedulePolicy;
        revision.DefaultOpenTime = request.DefaultOpenTime;
        revision.DefaultCloseTime = request.DefaultCloseTime;
        revision.DefaultAllowedDurationMinutes = string.IsNullOrWhiteSpace(request.DefaultAllowedDurationMinutes)
            ? null
            : RentalAllowedDurations.Normalize(request.DefaultAllowedDurationMinutes);
        revision.DefaultPricings = request.DefaultPricings?.Select(ToDefaultPricing).ToList();
        revision.DefaultRequiresDeposit = request.DefaultRequiresDeposit;
        revision.DefaultMaxAdvanceBookingDays = request.DefaultMaxAdvanceBookingDays;
        if (revisionId != null) { revision.Version++; revision.Touch(); }
        await SaveAsync(ct); Audit("revision.draft.saved", revision.Id); return revision;
    }

    public async Task<VisualRevisionDto> PublishAsync(Guid templateId, Guid revisionId, CancellationToken ct)
    {
        var template = await db.Set<ResourceVisualTemplate>().SingleOrDefaultAsync(x => x.Id == templateId, ct) ?? throw Missing();
        if (!template.IsActive) throw new ResourceVisualException("VISUAL_TEMPLATE_ARCHIVED", "Archived templates cannot publish revisions.");
        var revision = await db.Set<ResourceVisualTemplateRevision>().SingleOrDefaultAsync(x => x.Id == revisionId && x.TemplateId == templateId, ct) ?? throw Missing();
        if (revision.Status == ResourceVisualRevisionStatus.Published) return RevisionDto(revision);
        try { ResourceVisualArt.PreparePublication(revision, DateTimeOffset.UtcNow); }
        catch (ArgumentException ex) { throw new ResourceVisualException("SVG_UNSAFE", ex.Message); }
        revision.Version++; revision.Touch(); await SaveAsync(ct); Audit("revision.published", revision.Id); return RevisionDto(revision);
    }

    public async Task<IReadOnlyList<VisualTagDto>> TagsAsync(bool activeOnly, CancellationToken ct)
    {
        if (activeOnly) Tenant();
        return (await db.Set<ResourceVisualTag>().AsNoTracking().Where(x => !activeOnly || x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Slug).ToArrayAsync(ct)).Select(TagDto).ToArray();
    }

    public async Task<VisualTagDto> SaveTagAsync(Guid? id, TagRequest request, CancellationToken ct)
    {
        ValidateSlug(request.Slug); ValidateTranslations(request.NameI18n, 200, true);
        if (!Enum.IsDefined(request.Kind) || !FacetPattern().IsMatch(request.Facet ?? "") || (request.Kind == ResourceVisualTagKind.Segment) != (request.Facet == "segment")) throw Invalid("Tag kind and facet must agree.");
        var entity = id == null ? new ResourceVisualTag { Slug = request.Slug, Kind = request.Kind, Facet = request.Facet! } : await db.Set<ResourceVisualTag>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing();
        if (entity.Slug != request.Slug || entity.Kind != request.Kind || entity.Facet != request.Facet) throw Invalid("Tag slug, kind and facet are immutable.");
        if (await db.Set<ResourceVisualTag>().AnyAsync(x => x.Slug == request.Slug && x.Id != entity.Id, ct)) throw Invalid("Tag slug is already used.");
        entity.NameI18n = new(request.NameI18n); entity.IsActive = request.IsActive; entity.SortOrder = request.SortOrder;
        if (id == null) db.Add(entity); else entity.Touch(); await SaveAsync(ct); Audit("tag.saved", entity.Id); return TagDto(entity);
    }

    public async Task DeleteTagAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.Set<ResourceVisualTag>().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw Missing();
        if (await db.Set<ResourceVisualTemplateTag>().AnyAsync(x => x.TagId == id, ct) || await db.Set<TenantResourceSegment>().IgnoreQueryFilters().AnyAsync(x => x.TagId == id, ct)) { entity.IsActive = false; entity.Touch(); }
        else db.Remove(entity);
        await SaveAsync(ct); Audit("tag.deleted.or.archived", id);
    }

    public async Task<IReadOnlyList<VisualTagDto>> SegmentsAsync(Guid? explicitTenant, CancellationToken ct)
    {
        var tenant = explicitTenant ?? Tenant();
        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(x => x.Id == tenant, ct)) throw new ResourceVisualException("SEGMENT_INVALID", "Tenant not found.", 404);
        var ids = await db.Set<TenantResourceSegment>().IgnoreQueryFilters().Where(x => x.TenantId == tenant).Select(x => x.TagId).ToArrayAsync(ct);
        return (await db.Set<ResourceVisualTag>().AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.SortOrder).ToArrayAsync(ct)).Select(TagDto).ToArray();
    }

    public async Task<IReadOnlyList<VisualTagDto>> ReplaceSegmentsAsync(Guid? explicitTenant, SegmentsRequest request, CancellationToken ct)
    {
        var tenant = explicitTenant ?? Tenant();
        if (explicitTenant == null) await trialGuard.EnsureWritableAsync(ct);
        if (request.SegmentIds == null || request.SegmentIds.Length > 100) throw new ResourceVisualException("SEGMENT_INVALID", "At most 100 segments are allowed.");
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM core.tenants WHERE id = {tenant} FOR UPDATE", ct);
        await SegmentsAsync(tenant, ct);
        var ids = request.SegmentIds.Distinct().ToArray();
        if (await db.Set<ResourceVisualTag>().CountAsync(x => ids.Contains(x.Id) && x.Kind == ResourceVisualTagKind.Segment && x.IsActive, ct) != ids.Length) throw new ResourceVisualException("SEGMENT_INVALID", "Segments must reference active global segment tags.");
        var existing = await db.Set<TenantResourceSegment>().IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToListAsync(ct);
        db.RemoveRange(existing.Where(x => !ids.Contains(x.TagId)));
        db.AddRange(ids.Where(id => existing.All(x => x.TagId != id)).Select(id => new TenantResourceSegment { TenantId = tenant, TagId = id }));
        await SaveAsync(ct);
        var result = await SegmentsAsync(tenant, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        logger.LogInformation("ResourceVisualAudit Action={Action} TenantId={TenantId}", "tenant.segments.replaced", tenant);
        return result;
    }

    public async Task<ResourceVisualTemplateRevision?> PublishedArtAsync(Guid id, CancellationToken ct) => await db.Set<ResourceVisualTemplateRevision>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == ResourceVisualRevisionStatus.Published, ct);
    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ResourceVisualException("VISUAL_TEMPLATE_INVALID", "Catalog version changed; reload before saving.", 409); }
        catch (DbUpdateException) { throw new ResourceVisualException("VISUAL_TEMPLATE_INVALID", "Catalog data conflicts with a concurrent change or referenced row.", 409); }
    }
    private static void CheckVersion(long? expected, long current) { if (expected.HasValue && expected != current) throw new ResourceVisualException("VISUAL_TEMPLATE_INVALID", "Catalog version changed; reload before saving.", 409); }
    private void Audit(string action, Guid id) => logger.LogInformation("ResourceVisualAudit Action={Action} Id={Id}", action, id);
    private static void ValidateSlug(string value) { if ((value?.Length ?? 0) > 80 || !SlugPattern().IsMatch(value ?? "")) throw Invalid("A lowercase stable slug of at most 80 characters is required."); }
    private static void ValidateTranslations(Dictionary<string, string?>? values, int maximum, bool required)
    {
        if (values == null || values.Count > 3 || values.Any(x => x.Key is not ("pt-BR" or "en" or "es") || x.Value?.Length > maximum) || (required && (values.Count == 0 || values.Values.Any(string.IsNullOrWhiteSpace)))) throw Invalid("Translations must use pt-BR/en/es and respect length limits.");
    }
    private static void ValidateRevision(RevisionRequest r)
    {
        if (r.AspectRatio is <= 0 or > 100 || !ColorPattern().IsMatch(r.DefaultColor ?? "") || (r.DefaultIcon?.Length ?? 0) > 80 || !SlugPattern().IsMatch(r.DefaultIcon ?? "") || r.SuggestedSlotMinutes is <= 0 or > 1440 || r.SuggestedCapacityPeople is <= 0 or > 100000 || r.ViewBox?.Length > 100) throw Invalid("Invalid visual revision metadata.");
        if (r.DefaultMaxAdvanceBookingDays is < 0 or > 3650 || r.DefaultAllowedDurationMinutes?.Length > 128)
            throw Invalid("Invalid visual revision defaults.");
        if (r.DefaultSchedulePolicy == Platform.Core.Domain.Enums.SchedulePolicy.OpenHours && (r.DefaultOpenTime is null || r.DefaultCloseTime is null || r.DefaultCloseTime <= r.DefaultOpenTime))
            throw Invalid("OpenHours defaults require an ordered open and close time.");
        if (r.DefaultSchedulePolicy != Platform.Core.Domain.Enums.SchedulePolicy.OpenHours && (r.DefaultOpenTime is not null || r.DefaultCloseTime is not null || r.DefaultAllowedDurationMinutes is not null))
            throw Invalid("Hours defaults require the OpenHours schedule policy.");
        if (!string.IsNullOrWhiteSpace(r.DefaultAllowedDurationMinutes))
        {
            try { RentalAllowedDurations.Normalize(r.DefaultAllowedDurationMinutes); }
            catch (ArgumentException ex) { throw Invalid(ex.Message); }
        }
        if (r.DefaultPricings is { Count: > 100 }) throw Invalid("At most 100 pricing defaults are allowed.");
        if (r.DefaultPricings is not null)
        {
            try { TemplateDefaultPricing.Validate(r.DefaultPricings); }
            catch (ArgumentException ex) { throw Invalid(ex.Message); }
        }
        try
        {
            ResourceSvgSanitizer.Sanitize(r.FloorSvg, r.ViewBox!);
            if (r.LinesSvg != null) ResourceSvgSanitizer.Sanitize(r.LinesSvg, r.ViewBox!);
            if (r.NetSvg != null) ResourceSvgSanitizer.Sanitize(r.NetSvg, r.ViewBox!);
        }
        catch (ArgumentException ex) { throw new ResourceVisualException("SVG_UNSAFE", ex.Message); }
    }
    private static ResourceVisualDefaultPricing ToDefaultPricing(CreateRentalPricingDto value) => new()
    {
        DayOfWeek = value.DayOfWeek,
        StartTime = value.StartTime,
        EndTime = value.EndTime,
        PricePerHour = value.PricePerHour,
        RequiresDeposit = value.RequiresDeposit,
        DepositPercentage = value.DepositPercentage,
    };

    private static CreateRentalPricingDto ToPricingDto(ResourceVisualDefaultPricing value) => new()
    {
        DayOfWeek = value.DayOfWeek,
        StartTime = value.StartTime,
        EndTime = value.EndTime,
        PricePerHour = value.PricePerHour,
        RequiresDeposit = value.RequiresDeposit,
        DepositPercentage = value.DepositPercentage,
    };

    private sealed record RevisionMetadata(Guid Id, Guid TemplateId, int RevisionNo, ResourceVisualRevisionStatus Status, string ViewBox, decimal AspectRatio, string DefaultColor, string DefaultIcon, int? SuggestedSlotMinutes, int? SuggestedCapacityPeople, Platform.Core.Domain.Enums.SchedulePolicy? DefaultSchedulePolicy, TimeOnly? DefaultOpenTime, TimeOnly? DefaultCloseTime, string? DefaultAllowedDurationMinutes, List<ResourceVisualDefaultPricing>? DefaultPricings, bool? DefaultRequiresDeposit, int? DefaultMaxAdvanceBookingDays, string ArtSha256, long Version)
    {
        public VisualRevisionDto ToDto() => new(Id, TemplateId, RevisionNo, Status, ViewBox, AspectRatio, DefaultColor, DefaultIcon, SuggestedSlotMinutes, SuggestedCapacityPeople, DefaultSchedulePolicy, DefaultOpenTime, DefaultCloseTime, DefaultAllowedDurationMinutes, DefaultPricings?.Select(ToPricingDto).ToArray(), DefaultRequiresDeposit, DefaultMaxAdvanceBookingDays, ArtSha256, $"/api/public/resource-visual-revisions/{Id}/art.svg?v={ArtSha256}", Version);
    }
    private static VisualTemplateDto ToDto(ResourceVisualTemplate t, IEnumerable<ResourceVisualTag> tags, ResourceVisualTemplateRevision? revision) => new(t.Id, t.Slug, t.NameI18n, t.DescriptionI18n, t.IsActive, t.SortOrder, t.Version, tags.Select(TagDto).ToArray(), revision == null ? null : RevisionDto(revision));
    private static VisualTagDto TagDto(ResourceVisualTag t) => new(t.Id, t.Slug, t.Kind, t.Facet, t.NameI18n, t.IsActive, t.SortOrder);
    public static VisualRevisionDto RevisionDto(ResourceVisualTemplateRevision r) => new(r.Id, r.TemplateId, r.RevisionNo, r.Status, r.ViewBox, r.AspectRatio, r.DefaultColor, r.DefaultIcon, r.SuggestedSlotMinutes, r.SuggestedCapacityPeople, r.DefaultSchedulePolicy, r.DefaultOpenTime, r.DefaultCloseTime, r.DefaultAllowedDurationMinutes, r.DefaultPricings?.Select(ToPricingDto).ToArray(), r.DefaultRequiresDeposit, r.DefaultMaxAdvanceBookingDays, r.ArtSha256, r.Status == ResourceVisualRevisionStatus.Published ? $"/api/public/resource-visual-revisions/{r.Id}/art.svg?v={r.ArtSha256}" : null, r.Version);
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")] private static partial Regex SlugRegex();
    private static Regex SlugPattern() => SlugRegex();
    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")] private static partial Regex FacetPattern();
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")] private static partial Regex ColorPattern();
}
