using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public enum ResourceVisualRevisionStatus { Draft, Published }
public enum ResourceVisualTagKind { Segment, Attribute }

public sealed class ResourceVisualTemplate : Entity
{
    public string Slug { get; set; } = "";
    public Dictionary<string, string?> NameI18n { get; set; } = [];
    public Dictionary<string, string?> DescriptionI18n { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public long Version { get; set; } = 1;
    public ResourceVisualTemplate() { }
    public ResourceVisualTemplate(Guid id, DateTimeOffset createdAt) { Id = id; CreatedAt = createdAt; }
}

public sealed class ResourceVisualTemplateRevision : Entity
{
    public Guid TemplateId { get; set; }
    public int RevisionNo { get; set; }
    public ResourceVisualRevisionStatus Status { get; set; }
    public string FloorSvg { get; set; } = "";
    public string? LinesSvg { get; set; }
    public string? NetSvg { get; set; }
    public string ViewBox { get; set; } = "0 0 100 100";
    public decimal AspectRatio { get; set; } = 1;
    public string DefaultColor { get; set; } = "#D9D9D9";
    public string DefaultIcon { get; set; } = "square";
    public int? SuggestedSlotMinutes { get; set; } = 60;
    public int? SuggestedCapacityPeople { get; set; }
    public string ArtSha256 { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public long Version { get; set; } = 1;
    public ResourceVisualTemplateRevision() { }
    public ResourceVisualTemplateRevision(Guid id, DateTimeOffset createdAt) { Id = id; CreatedAt = createdAt; }
}

public sealed class ResourceVisualTag : Entity
{
    public string Slug { get; set; } = "";
    public ResourceVisualTagKind Kind { get; set; }
    public string Facet { get; set; } = "";
    public Dictionary<string, string?> NameI18n { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public ResourceVisualTag() { }
    public ResourceVisualTag(Guid id, DateTimeOffset createdAt) { Id = id; CreatedAt = createdAt; }
}

public sealed class ResourceVisualTemplateTag
{
    public Guid TemplateId { get; set; }
    public Guid TagId { get; set; }
}

public sealed class TenantResourceSegment : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid TagId { get; set; }
    public ResourceVisualTagKind TagKind { get; set; } = ResourceVisualTagKind.Segment;
}
