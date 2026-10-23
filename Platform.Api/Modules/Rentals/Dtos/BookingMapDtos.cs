using System.Text.Json;

namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record BookingMapItemDto(Guid Id, Guid RentalAssetId, string AssetName,
    double XPercent, double YPercent, double WidthPercent, double HeightPercent, int ZIndex,
    short Rotation, bool LockAspectRatio, Guid? VisualTemplateRevisionId, int? CapacityPeople);

public sealed record BookingMapDecorationDto(Guid Id, string Kind, string? Label, string? IconKey,
    double XPercent, double YPercent, double WidthPercent, double HeightPercent, short Rotation,
    int ZIndex, JsonElement Style, JsonElement? Geometry);

public sealed record BookingMapCompositionDto(Guid Id, Guid? UnitId, string Name, bool IsActive,
    int SortOrder, double AspectRatio, double WidthPercent, long Version,
    IReadOnlyList<BookingMapItemDto> Items, IReadOnlyList<BookingMapDecorationDto> Decorations);

public sealed record SaveBookingMapItemRequest
{
    public Guid? Id { get; init; }
    public required Guid RentalAssetId { get; init; }
    public required double XPercent { get; init; }
    public required double YPercent { get; init; }
    public required double WidthPercent { get; init; }
    public required double HeightPercent { get; init; }
    public short Rotation { get; init; }
    public bool LockAspectRatio { get; init; } = true;
    public int ZIndex { get; init; }
}

public sealed record SaveBookingMapDecorationRequest
{
    public Guid? Id { get; init; }
    public required string Kind { get; init; }
    public string? Label { get; init; }
    public string? IconKey { get; init; }
    public required double XPercent { get; init; }
    public required double YPercent { get; init; }
    public required double WidthPercent { get; init; }
    public required double HeightPercent { get; init; }
    public short Rotation { get; init; }
    public int ZIndex { get; init; }
    public JsonElement Style { get; init; }
    public JsonElement? Geometry { get; init; }
}

public sealed record SaveBookingMapCompositionRequest
{
    public required long ExpectedVersion { get; init; }
    public Guid? UnitId { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; } = true;
    public int SortOrder { get; init; }
    public double AspectRatio { get; init; } = 1.6;
    public double WidthPercent { get; init; } = 100;
    public IReadOnlyList<SaveBookingMapItemRequest> Items { get; init; } = [];
    public IReadOnlyList<SaveBookingMapDecorationRequest> Decorations { get; init; } = [];
}

public sealed record BookingMapSettingsDto(string AvailableColor, string TakenColor,
    string UnavailableColor, string SelectedColor, bool ShowPriceOnBlock, string TimeFilterMode,
    string DefaultView, Guid? DefaultLayoutId, long Version);

public sealed record SaveBookingMapSettingsRequest
{
    public required long ExpectedVersion { get; init; }
    public required string AvailableColor { get; init; }
    public required string TakenColor { get; init; }
    public required string UnavailableColor { get; init; }
    public required string SelectedColor { get; init; }
    public bool ShowPriceOnBlock { get; init; } = true;
    public string TimeFilterMode { get; init; } = "Periods";
    public string DefaultView { get; init; } = "Map";
    public Guid? DefaultLayoutId { get; init; }
}

public sealed record PublicBookingMapSummaryDto(Guid Id, Guid? UnitId, string Name,
    double AspectRatio, double WidthPercent, int SortOrder, long Version);
public sealed record PublicBookingMapListDto(IReadOnlyList<PublicBookingMapSummaryDto> Maps,
    Guid? DefaultLayoutId, BookingMapSettingsDto Settings);
public sealed record PublicVisualRevisionDto(Guid Id, Guid TemplateId, int RevisionNo,
    string FloorSvg, string? LinesSvg, string? NetSvg, string ViewBox, decimal AspectRatio,
    string DefaultColor, string DefaultIcon, string ArtSha256);
public sealed record PublicBookingMapDto(Guid Id, Guid? UnitId, string Name, double AspectRatio,
    double WidthPercent, long Version, IReadOnlyList<BookingMapItemDto> Items,
    IReadOnlyList<BookingMapDecorationDto> Decorations, IReadOnlyList<PublicVisualRevisionDto> VisualRevisions);

/// <summary>Operational DEV seed: only existing rental resource ids, never asset ids.</summary>
public sealed record SeedDemoBookingMapRequest
{
    public Guid? UnitId { get; init; }
    public string Name { get; init; } = "Mapa demonstrativo";
    public double AspectRatio { get; init; } = 1.75;
    public IReadOnlyList<SaveBookingMapItemRequest> Items { get; init; } = [];
    public IReadOnlyList<SaveBookingMapDecorationRequest> Decorations { get; init; } = [];
}
