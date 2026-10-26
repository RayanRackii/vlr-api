using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Serialization;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class RentalAssetService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    PolicyResolver? policyResolver = null) : IRentalAssetService
{
    private readonly PolicyResolver policies = policyResolver ?? new(dbContext);

    public async Task<IReadOnlyList<RentalAssetResponse>> ListRentableAsync(
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var tenantPolicy = await policies.ReadTenantAsync(tenantId, cancellationToken);
        var categories = await policies.ReadCategoriesAsync(tenantId, cancellationToken);

        var configs = await dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
                .ThenInclude(a => a.Category)
            .Where(r => r.TenantId == tenantId && r.Asset.TenantId == tenantId && r.IsActive && r.Asset.IsRentable && r.Asset.Status != Platform.Core.Domain.Enums.AssetStatus.Inactive)
            .OrderBy(r => r.Asset.Name)
            .ToListAsync(cancellationToken);

        return configs.Select(rental => ToResponse(rental, categories.GetValueOrDefault(rental.Asset.CategoryId), tenantPolicy)).ToList();
    }

    public async Task<RentalAssetResponse?> GetByAssetIdAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var tenantPolicy = await policies.ReadTenantAsync(tenantId, cancellationToken);
        var categories = await policies.ReadCategoriesAsync(tenantId, cancellationToken);

        var config = await dbContext.RentalAssets
            .AsNoTracking()
            .Include(r => r.Asset)
                .ThenInclude(a => a.Category)
            .FirstOrDefaultAsync(
                r => r.TenantId == tenantId && r.Asset.TenantId == tenantId && r.AssetId == assetId && r.IsActive && r.Asset.IsRentable,
                cancellationToken);

        return config is null ? null : ToResponse(config, categories.GetValueOrDefault(config.Asset.CategoryId), tenantPolicy);
    }

    public async Task<RentalAssetResponse> UpdateSchedulePolicyAsync(
        Guid rentalAssetId,
        UpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        if (request.SchedulePolicy == SchedulePolicy.OpenHours)
        {
            ValidatePolicy(request.SchedulePolicy, request.OpenTime, request.CloseTime);
        }

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await RentalAssetLocks.LockByRentalAssetIdAsync(
                dbContext, tenantId, rentalAssetId, cancellationToken);

            var rental = await dbContext.RentalAssets
                .Include(r => r.Asset)
                    .ThenInclude(a => a.Category)
                .FirstOrDefaultAsync(
                    r => r.Id == rentalAssetId && r.TenantId == tenantId && r.IsActive,
                    cancellationToken)
                ?? throw new KeyNotFoundException("Rentable was not found.");

            if (request.CancelCutoffHours.IsSpecified)
                rental.CancelCutoffHours = PolicyResolver.ValidateCancelCutoffHours(request.CancelCutoffHours.Value);
            rental.SchedulePolicy = request.SchedulePolicy;
            rental.OpenTime = request.OpenTime;
            rental.CloseTime = request.CloseTime;
            rental.AllowedDurationMinutes = NormalizeAllowedDurations(request, rental.AllowedDurationMinutes,
                await SlotGranularityMinutesAsync(tenantId, rental.Id, request.SchedulePolicy, cancellationToken));
            rental.Touch();

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var tenantPolicy = await policies.ReadTenantAsync(tenantId, cancellationToken);
            var categories = await policies.ReadCategoriesAsync(tenantId, cancellationToken);
            return ToResponse(rental, categories.GetValueOrDefault(rental.Asset.CategoryId), tenantPolicy);
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

    public async Task<BulkUpdateRentalSchedulePolicyResponseDto> UpdateSchedulePolicyBulkAsync(
        BulkUpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var ids = request.RentalAssetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            throw new ArgumentException("At least one rentable is required.");
        }

        ValidatePolicy(request.SchedulePolicy, request.OpenTime, request.CloseTime);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await RentalAssetLocks.LockByRentalAssetIdsAsync(
                dbContext, tenantId, ids, cancellationToken);

            var rentals = await dbContext.RentalAssets
                .Include(r => r.Asset)
                    .ThenInclude(a => a.Category)
                .Where(r => r.TenantId == tenantId && ids.Contains(r.Id) && r.IsActive)
                .ToListAsync(cancellationToken);

            if (rentals.Count != ids.Count)
            {
                throw new KeyNotFoundException("One or more rentables were not found.");
            }

            var cutoff = request.CancelCutoffHours.IsSpecified
                ? PolicyResolver.ValidateCancelCutoffHours(request.CancelCutoffHours.Value) : null;
            foreach (var rental in rentals)
            {
                if (request.CancelCutoffHours.IsSpecified)
                    rental.CancelCutoffHours = cutoff;
                rental.SchedulePolicy = request.SchedulePolicy;
                rental.OpenTime = request.OpenTime;
                rental.CloseTime = request.CloseTime;
                rental.AllowedDurationMinutes = NormalizeAllowedDurations(request, rental.AllowedDurationMinutes,
                await SlotGranularityMinutesAsync(tenantId, rental.Id, request.SchedulePolicy, cancellationToken));
                rental.Touch();
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var tenantPolicy = await policies.ReadTenantAsync(tenantId, cancellationToken);
            var categories = await policies.ReadCategoriesAsync(tenantId, cancellationToken);
            var items = rentals
                .OrderBy(r => r.Asset.Name)
                .Select(rental => ToResponse(rental, categories.GetValueOrDefault(rental.Asset.CategoryId), tenantPolicy))
                .ToList();

            return new BulkUpdateRentalSchedulePolicyResponseDto(items.Count, items);
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

    public async Task<RentalAssetResponse> SetMaxAdvanceBookingDaysAsync(
        Guid rentalAssetId,
        JsonOptional<int> maxAdvanceBookingDays,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        var horizon = maxAdvanceBookingDays.IsSpecified
            ? BookingPolicyText.NormalizeAdvanceDays(maxAdvanceBookingDays.Value)
            : null;

        var rental = await dbContext.RentalAssets
            .Include(r => r.Asset)
                .ThenInclude(a => a.Category)
            .FirstOrDefaultAsync(
                r => r.Id == rentalAssetId && r.TenantId == tenantId && r.IsActive && r.Asset.IsRentable,
                cancellationToken)
            ?? throw new KeyNotFoundException("Rentable was not found.");

        if (maxAdvanceBookingDays.IsSpecified)
        {
            rental.MaxAdvanceBookingDays = horizon;
            rental.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var tenantPolicy = await policies.ReadTenantAsync(tenantId, cancellationToken);
        var categories = await policies.ReadCategoriesAsync(tenantId, cancellationToken);
        return ToResponse(rental, categories.GetValueOrDefault(rental.Asset.CategoryId), tenantPolicy);
    }

    private static void ValidatePolicy(
        SchedulePolicy policy,
        TimeOnly? openTime,
        TimeOnly? closeTime)
    {
        if (policy != SchedulePolicy.OpenHours)
        {
            return;
        }

        if (openTime is null || closeTime is null)
        {
            throw new ArgumentException("OpenHours requires OpenTime and CloseTime.");
        }

        if (closeTime <= openTime)
        {
            throw new ArgumentException("CloseTime must be after OpenTime.");
        }
    }

    private static string? NormalizeAllowedDurations(
        UpdateRentalSchedulePolicyRequestDto request,
        string? storedAllowedDurationMinutes,
        int slotMinutes)
    {
        var value = request.AllowedDurationMinutes.IsSpecified
            ? request.AllowedDurationMinutes.Value
            : storedAllowedDurationMinutes;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var openingWindowMinutes = request.SchedulePolicy == SchedulePolicy.OpenHours
            ? (int?)OpenHoursRules.DailyWindowMinutes(request.OpenTime!.Value, request.CloseTime!.Value)
            : null;
        return RentalAllowedDurations.Normalize(value, slotMinutes, openingWindowMinutes);
    }

    private static string? NormalizeAllowedDurations(
        BulkUpdateRentalSchedulePolicyRequestDto request,
        string? storedAllowedDurationMinutes,
        int slotMinutes)
    {
        var value = request.AllowedDurationMinutes.IsSpecified
            ? request.AllowedDurationMinutes.Value
            : storedAllowedDurationMinutes;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var openingWindowMinutes = request.SchedulePolicy == SchedulePolicy.OpenHours
            ? (int?)OpenHoursRules.DailyWindowMinutes(request.OpenTime!.Value, request.CloseTime!.Value)
            : null;
        return RentalAllowedDurations.Normalize(value, slotMinutes, openingWindowMinutes);
    }

    /// <summary>
    /// Booking granularity for duration validation: SlotGrid resources use the GCD of their
    /// active weekly template window lengths; OpenHours (or no templates) has no slot multiple (1).
    /// </summary>
    private async Task<int> SlotGranularityMinutesAsync(
        Guid tenantId, Guid rentalAssetId, SchedulePolicy policy, CancellationToken cancellationToken)
    {
        if (policy != SchedulePolicy.SlotGrid)
        {
            return 1;
        }

        var windows = await dbContext.ScheduleTemplates.AsNoTracking()
            .Where(t => t.TenantId == tenantId && t.RentalAssetId == rentalAssetId && t.IsActive)
            .Select(t => new { t.StartTime, t.EndTime })
            .ToListAsync(cancellationToken);
        var gcd = 0;
        foreach (var w in windows)
        {
            var minutes = OpenHoursRules.DailyWindowMinutes(w.StartTime, w.EndTime);
            gcd = Gcd(gcd, minutes);
        }

        return gcd > 0 ? gcd : 1;

        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId is { } id && id != Guid.Empty
            ? id : throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static RentalAssetResponse ToResponse(RentalAsset rental, BookingPolicy? categoryPolicy, BookingPolicy? tenantPolicy) =>
        new(
            rental.Id,
            rental.AssetId,
            rental.TenantId,
            rental.Asset.UnitId,
            rental.Asset.Name,
            rental.Type,
            rental.TotalQuantity,
            rental.IsActive,
            rental.RequiresDeposit,
            rental.SchedulePolicy,
            rental.OpenTime,
            rental.CloseTime,
            rental.AllowedDurationMinutes,
            rental.QueueEnabled,
            rental.QueueOpeningTime,
            rental.Asset.CategoryId,
            rental.Asset.Category?.Name,
            rental.CreatedAt,
            rental.UpdatedAt,
            rental.MaxAdvanceBookingDays,
            PolicyResolver.Resolve(rental, categoryPolicy, tenantPolicy).MaxAdvanceBookingDays,
            rental.VisualTemplateRevisionId,
            rental.CapacityPeople,
            rental.CancelCutoffHours,
            rental.Asset.FamilyId == Guid.Empty ? null : rental.Asset.FamilyId,
            PolicyResolver.Resolve(rental, categoryPolicy, tenantPolicy).CancelCutoffHours,
            rental.Asset.Location,
            rental.Asset.ScheduledDeletionAt);
}
