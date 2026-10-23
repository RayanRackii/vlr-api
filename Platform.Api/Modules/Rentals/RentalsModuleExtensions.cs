using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals;

public static class RentalsModuleExtensions
{
    public static IServiceCollection AddRentalsModule(this IServiceCollection services)
    {
        services.AddOptions<BookingUiOptions>().BindConfiguration(BookingUiOptions.SectionName);
        services.AddOptions<BookingMapDemoSeedOptions>().BindConfiguration(BookingMapDemoSeedOptions.SectionName);
        services.AddScoped<IDailyAvailabilityService, DailyAvailabilityService>();
        services.AddScoped<IRentalAssetService, RentalAssetService>();
        services.AddScoped<IRentalConfigurationSync, RentalConfigurationSync>();
        services.AddScoped<Platform.Core.Domain.Contracts.ResourceVisuals.IRentalVisualDefaults, RentalVisualDefaults>();
        services.AddScoped<IRentalPricingService, RentalPricingService>();
        services.AddScoped<IReservationService, ReservationService>();
        services.AddScoped<IOccupancyKindService, OccupancyKindService>();
        services.AddScoped<IScheduleService, ScheduleService>();
        services.AddScoped<ITeacherLessonService, TeacherLessonService>();
        services.AddSingleton<ITeacherLessonScope, FiccTeacherLessonScope>();
        services.AddScoped<IRentalLayoutService, RentalLayoutService>();
        services.AddScoped<IReservationQueueService, ReservationQueueService>();
        services.AddScoped<IBookingPolicyService, BookingPolicyService>();
        services.AddScoped<IRentalsReadinessService, RentalsReadinessService>();
        services.AddScoped<IRentalsNotificationPublisher, RentalsNotificationPublisher>();

        return services;
    }
}
