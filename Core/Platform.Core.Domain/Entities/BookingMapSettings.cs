using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public sealed class BookingMapSettings : ITenantScoped
{
    public required Guid TenantId { get; set; }
    public string AvailableColor { get; set; } = "#15803D";
    public string TakenColor { get; set; } = "#B91C1C";
    public string UnavailableColor { get; set; } = "#6B7280";
    public string SelectedColor { get; set; } = "#1D4ED8";
    public bool ShowPriceOnBlock { get; set; } = true;
    public string TimeFilterMode { get; set; } = "Periods";
    public string DefaultView { get; set; } = "Map";
    public Guid? DefaultLayoutId { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}
