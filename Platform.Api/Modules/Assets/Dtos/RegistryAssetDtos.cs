using System.Text.Json.Serialization;
using Platform.Api.Serialization;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Assets.Dtos;

public sealed record RegistryAssetListItem(
    Guid Id,
    string Name,
    string Tag,
    Guid UnitId,
    Guid CategoryId,
    AssetStatus Status);

public sealed record RegistryCategoryListItem(
    Guid Id,
    string Name);

public sealed record CreateRentableRequest
{
    public required string Name { get; init; }

    /// <summary>
    /// Registry identifier. Optional: create generates one when omitted and
    /// update keeps the stored tag, because RentalAssetResponse does not expose it.
    /// </summary>
    public string? Tag { get; init; }

    public required Guid UnitId { get; init; }

    public required Guid CategoryId { get; init; }

    public required Guid FamilyId { get; init; }

    public RentalAssetType RentalType { get; init; } = RentalAssetType.Location;

    public int TotalQuantity { get; init; } = 1;

    /// <summary>
    /// Omitted keeps the legacy true default unless template defaults are explicitly applied.
    /// Explicit null is invalid.
    /// </summary>
    [JsonConverter(typeof(JsonOptionalBooleanConverter))]
    public JsonOptional<bool> RequiresDeposit { get; init; }

    public bool QueueEnabled { get; init; }

    public TimeOnly? QueueOpeningTime { get; init; }

    public string? Location { get; init; }

    /// <summary>
    /// Per-resource horizon override on create. Null inherits the tenant booking policy.
    /// </summary>
    /// <summary>
    /// Omitted inherits the tenant policy unless template defaults are explicitly applied.
    /// Explicit null keeps that inheritance.
    /// </summary>
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> MaxAdvanceBookingDays { get; init; }

    /// <summary>
    /// Per-resource cancellation cutoff in hours (0..8760). Omitted keeps the stored override
    /// (inherits on create); explicit null clears it so the resource inherits category/tenant policy.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> CancelCutoffHours { get; init; }

    public Guid? VisualTemplateRevisionId { get; init; }

    public int? CapacityPeople { get; init; }

    /// <summary>Applies suggested capacity only when no capacity has been configured.</summary>
    public bool ConfirmVisualSuggestions { get; init; }

    /// <summary>
    /// Applies values published on the selected visual revision only where this request omitted them.
    /// Omitted remains false for backwards compatibility.
    /// </summary>
    public bool ApplyTemplateDefaults { get; init; }
}

public sealed record UpdateRentableRequest
{
    public required string Name { get; init; }

    /// <summary>
    /// Registry identifier. Optional: create generates one when omitted and
    /// update keeps the stored tag, because RentalAssetResponse does not expose it.
    /// </summary>
    public string? Tag { get; init; }

    public required Guid UnitId { get; init; }

    public required Guid CategoryId { get; init; }

    public required Guid FamilyId { get; init; }

    public RentalAssetType RentalType { get; init; } = RentalAssetType.Location;

    public int TotalQuantity { get; init; } = 1;

    public bool RequiresDeposit { get; init; } = true;

    public bool QueueEnabled { get; init; }

    public TimeOnly? QueueOpeningTime { get; init; }

    public string? Location { get; init; }

    /// <summary>
    /// Per-resource horizon override. Omitted keeps the stored override.
    /// Explicit null clears it so the resource inherits the tenant policy.
    /// </summary>
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> MaxAdvanceBookingDays { get; init; }

    /// <summary>
    /// Per-resource cancellation cutoff in hours (0..8760). Omitted keeps the stored override
    /// (inherits on create); explicit null clears it so the resource inherits category/tenant policy.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> CancelCutoffHours { get; init; }

    /// <summary>Omitted preserves the fixed revision; explicit null clears it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonOptionalGuidConverter))]
    public JsonOptional<Guid> VisualTemplateRevisionId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> CapacityPeople { get; init; }

    public bool ConfirmVisualSuggestions { get; init; }
}
