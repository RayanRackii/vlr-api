using Platform.Core.Domain.Contracts.ResourceVisuals;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalVisualDefaults : IRentalVisualDefaults
{
    public string NormalizeAllowedDurations(string value, int slotMinutes = 60, int? openingWindowMinutes = null) =>
        RentalAllowedDurations.Normalize(value, slotMinutes, openingWindowMinutes);

    public void ValidatePricing(IReadOnlyList<CreateRentalPricingDto> pricings) => TemplateDefaultPricing.Validate(pricings);
}
