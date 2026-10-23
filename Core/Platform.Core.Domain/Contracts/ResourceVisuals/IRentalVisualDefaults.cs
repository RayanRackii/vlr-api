namespace Platform.Core.Domain.Contracts.ResourceVisuals;

/// <summary>Rental-specific validation used across the ResourceVisuals module boundary.</summary>
public interface IRentalVisualDefaults
{
    string NormalizeAllowedDurations(string value);
    void ValidatePricing(IReadOnlyList<CreateRentalPricingDto> pricings);
}
