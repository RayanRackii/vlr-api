using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals;

public static class RentalsModuleExtensions
{
    public static IServiceCollection AddRentalsModule(this IServiceCollection services)
    {
        services.AddScoped<IRentalAssetService, RentalAssetService>();
        services.AddScoped<IRentalPricingService, RentalPricingService>();
        services.AddScoped<IReservationService, ReservationService>();
        services.AddScoped<IOccupancyKindService, OccupancyKindService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<IRentalLayoutService, RentalLayoutService>();

        return services;
    }
}
