using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

internal static class TestReservationQueue
{
    public static ReservationQueueService Create(
        AppDbContext db,
        ITenantProvider tenantProvider,
        TimeProvider? timeProvider = null) =>
        new(
            db,
            tenantProvider,
            timeProvider ?? TimeProvider.System,
            NullLogger<ReservationQueueService>.Instance);
}
