using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Schedule write guards shared by <see cref="ScheduleTemplateWriter"/>, <see cref="DayPublisher"/>
/// and <see cref="ScheduleService"/>: asset lock transaction, tenant/rentable/kind checks,
/// time range and template collision.
/// </summary>
internal sealed class ScheduleWriteGuards(
    AppDbContext dbContext,
    ITenantProvider tenantProvider)
{
    internal async Task EnsureRentableAsync(
        Guid tenantId,
        Guid rentalAssetId,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.RentalAssets
            .AnyAsync(
                r => r.Id == rentalAssetId && r.TenantId == tenantId && r.IsActive,
                cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Rentable was not found.");
        }
    }

    internal async Task EnsureRentablesAsync(
        Guid tenantId,
        IReadOnlyList<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        if (rentalAssetIds.Count == 0)
        {
            throw new ArgumentException("At least one rentable is required.");
        }

        var found = await dbContext.RentalAssets
            .Where(r => rentalAssetIds.Contains(r.Id) && r.TenantId == tenantId && r.IsActive)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (found.Count != rentalAssetIds.Count)
        {
            throw new KeyNotFoundException("One or more rentables were not found.");
        }
    }

    internal async Task EnsureOccupancyKindAsync(
        Guid tenantId,
        Guid occupancyKindId,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.OccupancyKinds
            .AnyAsync(
                k => k.Id == occupancyKindId && k.TenantId == tenantId && k.IsActive,
                cancellationToken);
        if (!exists)
        {
            throw new KeyNotFoundException("Occupancy kind was not found.");
        }
    }

    internal async Task EnsureNoTemplateCollisionAsync(
        Guid tenantId,
        Guid rentalAssetId,
        DayOfWeek dayOfWeek,
        TimeOnly start,
        TimeOnly end,
        Guid occupancyKindId,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.ScheduleTemplates
            .Where(t => t.TenantId == tenantId
                        && t.RentalAssetId == rentalAssetId
                        && t.DayOfWeek == dayOfWeek)
            .ToListAsync(cancellationToken);

        EnsureNoTemplateCollision(
            existing, rentalAssetId, dayOfWeek, start, end, occupancyKindId, excludeId);
    }

    internal static void EnsureNoTemplateCollision(
        IEnumerable<ScheduleTemplate> existing,
        Guid rentalAssetId,
        DayOfWeek dayOfWeek,
        TimeOnly start,
        TimeOnly end,
        Guid occupancyKindId,
        Guid? excludeId)
    {
        foreach (var other in existing)
        {
            if (other.RentalAssetId != rentalAssetId || other.DayOfWeek != dayOfWeek)
            {
                continue;
            }

            if (excludeId is { } id && other.Id == id)
            {
                continue;
            }

            if (other.StartTime == start
                && other.EndTime == end
                && other.OccupancyKindId == occupancyKindId)
            {
                throw new ArgumentException(
                    "A schedule template with the same interval and occupancy kind already exists.");
            }

            if (other.OccupancyKindId == occupancyKindId
                && OccupancyPrecedence.IntervalsOverlap(start, end, other.StartTime, other.EndTime))
            {
                throw new ArgumentException(
                    "This occupancy kind already overlaps that interval on this weekday.");
            }
        }
    }

    internal async Task<IDbContextTransaction?> BeginAssetLockedTransactionAsync(
        Guid tenantId,
        IEnumerable<Guid> rentalAssetIds,
        CancellationToken cancellationToken)
    {
        var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await RentalAssetLocks.LockByRentalAssetIdsAsync(
                dbContext,
                tenantId,
                rentalAssetIds,
                cancellationToken);
            return transaction;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    internal static async Task CommitIfPresentAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal static async Task RollbackIfPresentAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
        }
    }

    internal Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    internal static void ValidateTimeRange(TimeOnly start, TimeOnly end)
    {
        if (end <= start)
        {
            throw new ArgumentException("End time must be after start time.");
        }
    }
}
