using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>Validation shared by published visual defaults and their one-time application on resource creation.</summary>
public static class TemplateDefaultPricing
{
    public static void Validate(IReadOnlyList<CreateRentalPricingDto> pricings)
    {
        var windows = new HashSet<(DayOfWeek Day, TimeOnly Start, TimeOnly End)>();
        foreach (var row in pricings)
        {
            if (row.EndTime <= row.StartTime)
                throw new ArgumentException("Default pricing end time must be greater than its start time.");
            if (row.PricePerHour < 0)
                throw new ArgumentException("Default pricing price per hour cannot be negative.");
            if (row.DepositPercentage is < 0 or > 100)
                throw new ArgumentException("Default pricing deposit percentage must be between 0 and 100.");
            if (!windows.Add((row.DayOfWeek, row.StartTime, row.EndTime)))
                throw new ArgumentException("Default pricings contain duplicate windows for the same day.");
        }

        for (var i = 0; i < pricings.Count; i++)
        for (var j = i + 1; j < pricings.Count; j++)
        {
            var left = pricings[i];
            var right = pricings[j];
            if (left.DayOfWeek == right.DayOfWeek && left.StartTime < right.EndTime && left.EndTime > right.StartTime)
                throw new ArgumentException($"Default pricing windows overlap on {left.DayOfWeek}.");
        }
    }
}
