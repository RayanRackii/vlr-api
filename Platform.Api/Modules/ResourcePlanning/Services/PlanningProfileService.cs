using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>
/// Planning profile and weekly working hours of assets. Reads the asset registry directly through
/// the tenant-filtered context (planning never depends on the inventory module being entitled).
/// Every query carries the tenant id on top of the global query filter.
/// </summary>
public sealed class PlanningProfileService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    ITenantModuleAccessor tenantModuleAccessor) : IPlanningProfileService
{
    public const int MaxPrepOrCleanupMinutes = 1440;
    public const int MaxCapacity = 1000;
    public const int MaxWorkingWindows = 100;

    // Working-hours input is HH:mm only and the response echoes HH:mm, so a round trip is exact.
    private static readonly string[] TimeFormats = ["HH:mm"];

    public async Task<IReadOnlyList<PlanningAssetResponse>> ListAssetsAsync(
        string? search,
        Guid? unitId,
        string? plannable,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var filter = ParsePlannableFilter(plannable);
        var rentalsActive = await IsRentalsActiveAsync(cancellationToken);

        var query = dbContext.Assets
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ScheduledDeletionAt == null);

        if (unitId is { } unit)
        {
            query = query.Where(a => a.UnitId == unit);
        }

        var term = search?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(a => a.Name.ToLower().Contains(term) || a.Tag.ToLower().Contains(term));
        }

        if (filter == PlannableFilter.Plannable)
        {
            query = query.Where(a => dbContext.AssetPlanningProfiles.Any(p =>
                p.TenantId == tenantId && p.AssetId == a.Id && p.IsPlannable));
        }
        else if (filter == PlannableFilter.NotPlannable)
        {
            query = query.Where(a => !dbContext.AssetPlanningProfiles.Any(p =>
                p.TenantId == tenantId && p.AssetId == a.Id && p.IsPlannable));
        }

        var assets = await query
            .OrderBy(a => a.Tag)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.Tag,
                a.CategoryId,
                CategoryName = a.Category.Name,
                a.UnitId,
                UnitName = a.Unit.Name,
                a.Status,
                HasActiveRental = a.RentalConfiguration != null && a.RentalConfiguration.IsActive,
            })
            .ToListAsync(cancellationToken);

        var profiles = await dbContext.AssetPlanningProfiles
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId)
            .ToDictionaryAsync(p => p.AssetId, cancellationToken);

        var windowCounts = await dbContext.AssetWorkingHours
            .AsNoTracking()
            .Where(h => h.TenantId == tenantId)
            .GroupBy(h => h.AssetId)
            .Select(g => new { AssetId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AssetId, x => x.Count, cancellationToken);

        return assets
            .Select(a =>
            {
                profiles.TryGetValue(a.Id, out var profile);
                windowCounts.TryGetValue(a.Id, out var windows);
                return new PlanningAssetResponse(
                    a.Id,
                    a.Name,
                    a.Tag,
                    a.CategoryId,
                    a.CategoryName,
                    a.UnitId,
                    a.UnitName,
                    a.Status.ToString(),
                    profile is { IsPlannable: true },
                    a.HasActiveRental && rentalsActive,
                    profile?.HoursPolicy.ToString(),
                    windows);
            })
            .ToList();
    }

    public async Task<AssetPlanningProfileResponse?> GetProfileAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var asset = await FindAssetAsync(tenantId, assetId, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        var rentalBacked = asset.HasActiveRental && await IsRentalsActiveAsync(cancellationToken);

        var profile = await dbContext.AssetPlanningProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.AssetId == assetId, cancellationToken);

        var hours = await dbContext.AssetWorkingHours
            .AsNoTracking()
            .Where(h => h.TenantId == tenantId && h.AssetId == assetId)
            .ToListAsync(cancellationToken);

        return ToResponse(assetId, profile, rentalBacked, hours);
    }

    public async Task<AssetPlanningProfileResponse> UpsertProfileAsync(
        Guid assetId,
        PutAssetPlanningProfileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = EnsureTenantContext();
        var asset = await FindAssetAsync(tenantId, assetId, cancellationToken)
            ?? throw PlanningException.AssetNotFound(assetId);
        var rentalBacked = asset.HasActiveRental && await IsRentalsActiveAsync(cancellationToken);

        var policy = ParsePolicy(request.HoursPolicy);
        if (request.Capacity < 1 || request.Capacity > MaxCapacity)
        {
            throw PlanningException.Invalid($"Capacity must be between 1 and {MaxCapacity}.");
        }

        ValidateBuffer(request.DefaultPrepMinutes, "Default prep minutes");
        ValidateBuffer(request.DefaultCleanupMinutes, "Default cleanup minutes");
        var windows = ParseWindows(request.WorkingHours);

        if (rentalBacked && windows.Count > 0)
        {
            throw new PlanningException(
                StatusCodes.Status409Conflict,
                PlanningException.RentalScheduleAuthorityCode,
                "This asset is rental-backed: its rental schedule is the calendar authority, so working hours cannot be set here.");
        }

        // No rows with a policy other than Anytime means "closed all week", which would make the
        // asset unusable. Reject it for a plannable asset; a rental-backed asset takes its hours
        // from the rental schedule instead.
        if (request.IsPlannable && !rentalBacked && policy != AssetHoursPolicy.Anytime && windows.Count == 0)
        {
            throw new PlanningException(
                StatusCodes.Status422UnprocessableEntity,
                PlanningException.WorkingHoursRequiredCode,
                "Add at least one working-hours window, or choose the Anytime policy.");
        }

        await using var transaction = dbContext.Database.IsRelational() && dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;

        // The same asset-row lock the allocations take, so a profile or hours change never
        // interleaves with an allocation create or edit that validated against the old hours.
        await AssetRowLock.LockAsync(dbContext, tenantId, [assetId], cancellationToken);

        var profile = await dbContext.AssetPlanningProfiles
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.AssetId == assetId, cancellationToken);
        if (profile is null)
        {
            profile = new AssetPlanningProfile { TenantId = tenantId, AssetId = assetId };
            dbContext.AssetPlanningProfiles.Add(profile);
        }
        else
        {
            profile.Touch();
        }

        profile.IsPlannable = request.IsPlannable;
        profile.Capacity = request.Capacity;
        profile.HoursPolicy = policy;
        profile.DefaultPrepMinutes = request.DefaultPrepMinutes;
        profile.DefaultCleanupMinutes = request.DefaultCleanupMinutes;

        var existing = await dbContext.AssetWorkingHours
            .Where(h => h.TenantId == tenantId && h.AssetId == assetId)
            .ToListAsync(cancellationToken);
        dbContext.AssetWorkingHours.RemoveRange(existing);

        var added = windows
            .Select(w => new AssetWorkingHours
            {
                TenantId = tenantId,
                AssetId = assetId,
                DayOfWeek = w.Day,
                OpenTime = w.Open,
                CloseTime = w.Close,
            })
            .ToList();

        try
        {
            // Deletes first, then inserts: the unique (tenant, asset, day, open) index must never
            // see the old and the new window at once.
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.AssetWorkingHours.AddRange(added);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (PlanningPersistenceErrors.Translate(ex, assetId) is not null)
        {
            // A real concurrency or unique-key conflict (a concurrent first save of the same
            // asset, a colliding window) is retryable; an FK violation means the asset vanished.
            // Anything else is not caught here and propagates unchanged.
            throw PlanningPersistenceErrors.Translate(ex, assetId)!;
        }

        return ToResponse(assetId, profile, rentalBacked, added);
    }

    private static AssetPlanningProfileResponse ToResponse(
        Guid assetId,
        AssetPlanningProfile? profile,
        bool rentalBacked,
        IEnumerable<AssetWorkingHours> hours)
    {
        var windows = hours
            .OrderBy(h => h.DayOfWeek)
            .ThenBy(h => h.OpenTime)
            .Select(h => new AssetWorkingHoursWindowDto
            {
                DayOfWeek = h.DayOfWeek,
                OpenTime = h.OpenTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                CloseTime = h.CloseTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            })
            .ToList();

        return profile is null
            ? new AssetPlanningProfileResponse(
                assetId,
                IsConfigured: false,
                IsPlannable: false,
                IsRentalBacked: rentalBacked,
                Capacity: 1,
                HoursPolicy: AssetHoursPolicy.StartWithinHours.ToString(),
                DefaultPrepMinutes: 0,
                DefaultCleanupMinutes: 0,
                WorkingHours: windows)
            : new AssetPlanningProfileResponse(
                assetId,
                IsConfigured: true,
                IsPlannable: profile.IsPlannable,
                IsRentalBacked: rentalBacked,
                Capacity: profile.Capacity,
                HoursPolicy: profile.HoursPolicy.ToString(),
                DefaultPrepMinutes: profile.DefaultPrepMinutes,
                DefaultCleanupMinutes: profile.DefaultCleanupMinutes,
                WorkingHours: windows);
    }

    private static AssetHoursPolicy ParsePolicy(string? value)
    {
        foreach (var policy in Enum.GetValues<AssetHoursPolicy>())
        {
            if (string.Equals(policy.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return policy;
            }
        }

        throw PlanningException.Invalid(
            "Hours policy must be one of: Anytime, StartWithinHours, WithinHours.");
    }

    private static void ValidateBuffer(int minutes, string label)
    {
        if (minutes < 0 || minutes > MaxPrepOrCleanupMinutes)
        {
            throw PlanningException.Invalid($"{label} must be between 0 and {MaxPrepOrCleanupMinutes}.");
        }
    }

    private static List<(short Day, TimeOnly Open, TimeOnly Close)> ParseWindows(
        IReadOnlyList<AssetWorkingHoursWindowDto>? input)
    {
        var windows = new List<(short Day, TimeOnly Open, TimeOnly Close)>();
        if (input is null)
        {
            return windows;
        }

        if (input.Count > MaxWorkingWindows)
        {
            throw PlanningException.Invalid($"At most {MaxWorkingWindows} working-hours windows are allowed.");
        }

        foreach (var window in input)
        {
            if (window is null)
            {
                throw PlanningException.Invalid("A working-hours window is missing.");
            }

            if (window.DayOfWeek is < 0 or > 6)
            {
                throw PlanningException.Invalid("Day of week must be between 0 (Sunday) and 6 (Saturday).");
            }

            if (!TimeOnly.TryParseExact(window.OpenTime, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var open)
                || !TimeOnly.TryParseExact(window.CloseTime, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var close))
            {
                throw PlanningException.Invalid("Working-hours times must be in HH:mm format.");
            }

            if (close <= open)
            {
                throw PlanningException.Invalid("A working-hours window must close after it opens.");
            }

            windows.Add(((short)window.DayOfWeek, open, close));
        }

        foreach (var day in windows.GroupBy(w => w.Day))
        {
            var ordered = day.OrderBy(w => w.Open).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].Open < ordered[i - 1].Close)
                {
                    throw PlanningException.Invalid("Working-hours windows of the same day must not overlap.");
                }
            }
        }

        return windows;
    }

    private async Task<AssetRow?> FindAssetAsync(
        Guid tenantId,
        Guid assetId,
        CancellationToken cancellationToken) =>
        await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.Id == assetId && a.TenantId == tenantId && a.ScheduledDeletionAt == null)
            .Select(a => new AssetRow(
                a.Id,
                a.RentalConfiguration != null && a.RentalConfiguration.IsActive))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<bool> IsRentalsActiveAsync(CancellationToken cancellationToken)
    {
        var active = await tenantModuleAccessor.GetActiveModuleKeysAsync(cancellationToken);
        return active.Contains(PlatformModules.Rentals);
    }

    private Guid EnsureTenantContext() =>
        tenantProvider.TenantId is { } id && id != Guid.Empty
            ? id
            : throw new UnauthorizedAccessException("Tenant context is required.");

    private static PlannableFilter ParsePlannableFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return PlannableFilter.All;
        }

        if (value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return PlannableFilter.Plannable;
        }

        if (value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return PlannableFilter.NotPlannable;
        }

        throw PlanningException.Invalid("Plannable filter must be true, false or all.");
    }

    private enum PlannableFilter
    {
        All,
        Plannable,
        NotPlannable,
    }

    private sealed record AssetRow(Guid Id, bool HasActiveRental);
}
