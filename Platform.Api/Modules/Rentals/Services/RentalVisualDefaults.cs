using Platform.Core.Domain.Contracts.ResourceVisuals;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalVisualDefaults : IRentalVisualDefaults
{
    public string NormalizeAllowedDurations(string value) => RentalAllowedDurations.Normalize(value);

    public void ValidatePricing(IReadOnlyList<CreateRentalPricingDto> pricings) => TemplateDefaultPricing.Validate(pricings);
}
