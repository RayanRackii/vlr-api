using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>Serializes map/unit membership validation with resource unit changes.</summary>
public static class BookingMapLocks
{
    public static Task AcquireAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        if (db.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL") return Task.CompletedTask;
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Booking map locks require a transaction.");
        var key = "booking-maps:" + tenantId.ToString("D");
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
    }
}
