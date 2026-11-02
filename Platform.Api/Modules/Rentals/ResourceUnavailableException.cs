namespace Platform.Api.Modules.Rentals.Contracts;

/// <summary>
/// A requested resource/interval could not be reserved because it conflicts with existing or
/// tentative occupancy. Callers (e.g. Catalog's allocation flow) translate this into a
/// <c>409 resource_unavailable</c> response.
/// </summary>
public sealed class ResourceUnavailableException(Guid rentalAssetId, string reason)
    : InvalidOperationException(reason)
{
    public const string Code = "resource_unavailable";

    public Guid RentalAssetId { get; } = rentalAssetId;
}
