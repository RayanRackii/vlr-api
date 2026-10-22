using System.Globalization;
using System.Text;
using System.Text.Json;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence.Seed;

/// <summary>Version-one migration data. Never change published V1 art; add a new revision instead.</summary>
public static class ResourceVisualCatalogV1Sql
{
    public static string Up()
    {
        var sql = new StringBuilder();
        foreach (var tag in ResourceVisualCatalogSeed.Tags)
            Insert(sql, "core.resource_visual_tags", TagColumns, TagValues(tag));
        foreach (var template in ResourceVisualCatalogSeed.Templates)
            Insert(sql, "core.resource_visual_templates", TemplateColumns, TemplateValues(template));
        foreach (var revision in ResourceVisualCatalogSeed.Revisions)
            Insert(sql, "core.resource_visual_template_revisions", RevisionColumns, RevisionValues(revision),
                $"EXISTS (SELECT 1 FROM core.resource_visual_templates WHERE id={Literal(revision.TemplateId)})");
        foreach (var link in ResourceVisualCatalogSeed.TemplateTags)
            Insert(sql, "core.resource_visual_template_tags", "template_id,tag_id", $"{Literal(link.TemplateId)},{Literal(link.TagId)}",
                $"EXISTS (SELECT 1 FROM core.resource_visual_templates WHERE id={Literal(link.TemplateId)}) AND EXISTS (SELECT 1 FROM core.resource_visual_tags WHERE id={Literal(link.TagId)})");
        return sql.ToString();
    }

    public static string Down()
    {
        var sql = new StringBuilder("DO $seed_down$ BEGIN\n");
        foreach (var tag in ResourceVisualCatalogSeed.Tags)
            AssertUnchanged(sql, "core.resource_visual_tags", tag.Id, TagColumns, TagValues(tag));
        foreach (var template in ResourceVisualCatalogSeed.Templates)
            AssertUnchanged(sql, "core.resource_visual_templates", template.Id, TemplateColumns, TemplateValues(template));
        foreach (var revision in ResourceVisualCatalogSeed.Revisions)
            AssertUnchanged(sql, "core.resource_visual_template_revisions", revision.Id, RevisionColumns, RevisionValues(revision));
        var templateIds = Ids(ResourceVisualCatalogSeed.Templates.Select(x => x.Id));
        var revisionIds = Ids(ResourceVisualCatalogSeed.Revisions.Select(x => x.Id));
        var tagIds = Ids(ResourceVisualCatalogSeed.Tags.Select(x => x.Id));
        var expectedLinks = string.Join(",", ResourceVisualCatalogSeed.TemplateTags.Select(x => $"({Literal(x.TemplateId)},{Literal(x.TagId)})"));
        sql.AppendLine($"""
            IF EXISTS (SELECT 1 FROM rentals.rental_assets WHERE visual_template_revision_id IN ({revisionIds}))
                OR EXISTS (SELECT 1 FROM core.tenant_resource_segments WHERE tag_id IN ({tagIds}))
                OR EXISTS (SELECT 1 FROM core.resource_visual_template_revisions WHERE template_id IN ({templateIds}) AND id NOT IN ({revisionIds}))
                OR EXISTS (SELECT 1 FROM core.resource_visual_template_tags WHERE (template_id IN ({templateIds}) OR tag_id IN ({tagIds})) AND (template_id,tag_id) NOT IN ({expectedLinks})) THEN
                RAISE EXCEPTION 'RESOURCE_VISUAL_SEED_DOWN_BLOCKED: Seed data is referenced or extended. Export configuration and explicitly remove references before retrying; use the booking UI flag for operational rollback.';
            END IF;
            END $seed_down$;
            DELETE FROM core.resource_visual_template_tags WHERE template_id IN ({templateIds});
            DELETE FROM core.resource_visual_template_revisions WHERE id IN ({revisionIds});
            DELETE FROM core.resource_visual_templates WHERE id IN ({templateIds});
            DELETE FROM core.resource_visual_tags WHERE id IN ({tagIds});
            """);
        return sql.ToString();
    }

    private static void Insert(StringBuilder sql, string table, string columns, string values, string? condition = null) =>
        sql.AppendLine($"INSERT INTO {table} ({columns}) SELECT {values}{(condition == null ? "" : " WHERE " + condition)} ON CONFLICT DO NOTHING;");

    private static void AssertUnchanged(StringBuilder sql, string table, Guid id, string columns, string values) =>
        sql.AppendLine($"IF EXISTS (SELECT 1 FROM {table} WHERE id={Literal(id)} AND ROW({columns}) IS DISTINCT FROM ROW({values})) THEN RAISE EXCEPTION 'RESOURCE_VISUAL_SEED_DOWN_BLOCKED: Published seed or metadata was modified. Export configuration and review rollback manually.'; END IF;");

    private const string TagColumns = "id,slug,kind,facet,name_i18n,is_active,sort_order,created_at,updated_at";
    private const string TemplateColumns = "id,slug,name_i18n,description_i18n,is_active,sort_order,version,created_at,updated_at";
    private const string RevisionColumns = "id,template_id,revision_no,status,floor_svg,lines_svg,net_svg,view_box,aspect_ratio,default_color,default_icon,suggested_slot_minutes,suggested_capacity_people,art_sha256,published_at,version,created_at,updated_at";
    private static string TagValues(ResourceVisualTag t) => Values(t.Id, t.Slug, t.Kind.ToString(), t.Facet, Json(t.NameI18n), t.IsActive, t.SortOrder, t.CreatedAt, t.UpdatedAt);
    private static string TemplateValues(ResourceVisualTemplate t) => Values(t.Id, t.Slug, Json(t.NameI18n), Json(t.DescriptionI18n), t.IsActive, t.SortOrder, t.Version, t.CreatedAt, t.UpdatedAt);
    private static string RevisionValues(ResourceVisualTemplateRevision r) => Values(r.Id, r.TemplateId, r.RevisionNo, r.Status.ToString(), r.FloorSvg, r.LinesSvg, r.NetSvg, r.ViewBox, r.AspectRatio, r.DefaultColor, r.DefaultIcon, r.SuggestedSlotMinutes, r.SuggestedCapacityPeople, r.ArtSha256, r.PublishedAt, r.Version, r.CreatedAt, r.UpdatedAt);
    private sealed record JsonLiteral(string Value);
    private static JsonLiteral Json(Dictionary<string, string?> value) => new(JsonSerializer.Serialize(value));
    private static string Ids(IEnumerable<Guid> ids) => string.Join(",", ids.Select(id => Literal(id)));
    private static string Values(params object?[] values) => string.Join(",", values.Select(Literal));
    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        JsonLiteral json => Quote(json.Value) + "::jsonb",
        Guid id => Quote(id.ToString()) + "::uuid",
        DateTimeOffset time => Quote(time.ToString("O", CultureInfo.InvariantCulture)) + "::timestamptz",
        bool boolean => boolean ? "TRUE" : "FALSE",
        string text => Quote(text),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unsupported seed SQL value."),
    };
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
}
