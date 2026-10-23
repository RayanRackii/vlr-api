using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public sealed class LayoutDecoration : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }
    public required Guid LayoutId { get; set; }
    public string Kind { get; set; } = "Shape";
    public string? Label { get; set; }
    public string? IconKey { get; set; }
    public double XPercent { get; set; }
    public double YPercent { get; set; }
    public double WidthPercent { get; set; }
    public double HeightPercent { get; set; }
    public short Rotation { get; set; }
    public int ZIndex { get; set; }
    public string StyleJson { get; set; } = "{}";
    public string? GeometryJson { get; set; }
}
