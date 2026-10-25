using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Serialization;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

public sealed class BookingPolicyService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IBookingPolicyService
{
    public async Task<BookingPolicyResponseDto> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        // No row stays unlimited. Do not apply BookingPolicyDefaults here.
        var policy = await FindDefaultAsync(tenantId, tracking: false, cancellationToken);
        return ToDto(policy);
    }

    public async Task<BookingPolicyResponseDto> UpsertAsync(
        UpsertBookingPolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var text = BookingPolicyText.Sanitize(request.CancellationPolicyText);
        var limit = BookingPolicyText.NormalizeDailyLimit(request.MaxActiveReservationsPerCustomerPerDay);

        try
        {
            return await SaveAsync(tenantId, text, limit, request.MaxAdvanceBookingDays, request.CancelCutoffHours, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            return await SaveAsync(tenantId, text, limit, request.MaxAdvanceBookingDays, request.CancelCutoffHours, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<CategoryBookingPolicyResponseDto>> GetCategoriesAsync(
        CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var categories = await dbContext.AssetCategories.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.ScheduledDeletionAt == null
                && dbContext.RentalAssets.Any(r => r.TenantId == tenantId
                    && r.Asset.TenantId == tenantId && r.Asset.CategoryId == c.Id))
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(cancellationToken);
        var policies = await new PolicyResolver(dbContext).ReadCategoriesAsync(tenantId, cancellationToken);
        return categories.Select(c => new CategoryBookingPolicyResponseDto(c.Id, c.Name,
            policies.GetValueOrDefault(c.Id)?.MaxAdvanceBookingDays,
            policies.GetValueOrDefault(c.Id)?.CancelCutoffHours)).ToList();
    }

    public async Task<CategoryBookingPolicyResponseDto> UpsertCategoryAsync(
        Guid categoryId, UpsertCategoryBookingPolicyRequestDto request, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        var category = await dbContext.AssetCategories.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == categoryId, cancellationToken)
            ?? throw new KeyNotFoundException("Asset category was not found.");
        var days = BookingPolicyText.NormalizeAdvanceDays(request.MaxAdvanceBookingDays);
        var cutoff = PolicyResolver.ValidateCancelCutoffHours(request.CancelCutoffHours);
        try
        {
            await SaveCategoryAsync(tenantId, categoryId, days, cutoff, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            await SaveCategoryAsync(tenantId, categoryId, days, cutoff, cancellationToken);
        }
        return new(category.Id, category.Name, days, cutoff);
    }

    private async Task SaveCategoryAsync(Guid tenantId, Guid categoryId, int? days, int? cutoff,
        CancellationToken cancellationToken)
    {
        var policy = await dbContext.BookingPolicies.FirstOrDefaultAsync(
            p => p.TenantId == tenantId && p.AssetCategoryId == categoryId && p.ResourceType == null,
            cancellationToken);
        if (policy is null)
        {
            policy = new BookingPolicy { TenantId = tenantId, AssetCategoryId = categoryId };
            dbContext.BookingPolicies.Add(policy);
        }
        policy.MaxAdvanceBookingDays = days;
        policy.CancelCutoffHours = cutoff;
        policy.CancellationPolicyText = null;
        policy.MaxActiveReservationsPerCustomerPerDay = null;
        policy.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<BookingPolicyResponseDto> SaveAsync(
        Guid tenantId,
        string? text,
        int? limit,
        JsonOptional<int> horizon,
        JsonOptional<int> cancelCutoff,
        CancellationToken cancellationToken)
    {
        var policy = await FindDefaultAsync(tenantId, tracking: true, cancellationToken);
        // A tenant with no row is not a new tenant. An omitted horizon stays null.
        var resolvedHorizon = horizon.IsSpecified
            ? BookingPolicyText.NormalizeAdvanceDays(horizon.Value)
            : policy?.MaxAdvanceBookingDays;
        var resolvedCutoff = cancelCutoff.IsSpecified
            ? PolicyResolver.ValidateCancelCutoffHours(cancelCutoff.Value)
            : policy?.CancelCutoffHours;
        if (policy is null)
        {
            policy = new BookingPolicy
            {
                TenantId = tenantId,
                ResourceType = null,
                CancellationPolicyText = text,
                MaxActiveReservationsPerCustomerPerDay = limit,
                MaxAdvanceBookingDays = resolvedHorizon,
                CancelCutoffHours = resolvedCutoff,
            };
            dbContext.BookingPolicies.Add(policy);
        }
        else
        {
            policy.Replace(text, limit, resolvedHorizon);
            policy.CancelCutoffHours = resolvedCutoff;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToDto(policy);
    }

    private Task<BookingPolicy?> FindDefaultAsync(
        Guid tenantId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = dbContext.BookingPolicies
            .Where(policy => policy.TenantId == tenantId && policy.ResourceType == null && policy.AssetCategoryId == null);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return query.FirstOrDefaultAsync(cancellationToken);
    }

    private Guid RequireTenant()
    {
        if (tenantProvider.TenantId is not Guid tenantId || tenantId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("Tenant context is required.");
        }

        return tenantId;
    }

    private static BookingPolicyResponseDto ToDto(BookingPolicy? policy) =>
        new(
            policy?.CancellationPolicyText,
            policy?.MaxActiveReservationsPerCustomerPerDay,
            policy?.MaxAdvanceBookingDays,
            policy?.CancelCutoffHours);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation;
}
