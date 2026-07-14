using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals;

public static class RentalsModuleExtensions
{
    public static IServiceCollection AddRentalsModule(this IServiceCollection services)
    {
        services.AddScoped<IRentalAssetService, RentalAssetService>();
        services.AddScoped<IRentalPricingService, RentalPricingService>();
        services.AddScoped<IReservationService, ReservationService>();

        return services;
    }
}
