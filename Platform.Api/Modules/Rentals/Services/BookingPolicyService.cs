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
            return await SaveAsync(tenantId, text, limit, request.MaxAdvanceBookingDays, request.CancelCutoffHours,
                request.MinimumBookingLeadMinutes, request.DepositPercent, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            return await SaveAsync(tenantId, text, limit, request.MaxAdvanceBookingDays, request.CancelCutoffHours,
                request.MinimumBookingLeadMinutes, request.DepositPercent, cancellationToken);
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
        return categories.Select(c =>
        {
            var policy = policies.GetValueOrDefault(c.Id);
            return new CategoryBookingPolicyResponseDto(c.Id, c.Name,
                policy?.MaxAdvanceBookingDays,
                policy?.CancelCutoffHours,
                policy?.MinimumBookingLeadMinutes,
                policy?.DepositPercent);
        }).ToList();
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
        var lead = request.MinimumBookingLeadMinutes.IsSpecified
            ? BookingPolicy.NormalizeLeadMinutes(request.MinimumBookingLeadMinutes.Value)
            : null;
        var deposit = request.DepositPercent.IsSpecified
            ? BookingPolicy.NormalizeDepositPercent(request.DepositPercent.Value)
            : null;
        BookingPolicy policy;
        try
        {
            policy = await SaveCategoryAsync(tenantId, categoryId, days, cutoff,
                request.MinimumBookingLeadMinutes.IsSpecified, lead,
                request.DepositPercent.IsSpecified, deposit, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            policy = await SaveCategoryAsync(tenantId, categoryId, days, cutoff,
                request.MinimumBookingLeadMinutes.IsSpecified, lead,
                request.DepositPercent.IsSpecified, deposit, cancellationToken);
        }
        return new(category.Id, category.Name, policy.MaxAdvanceBookingDays, policy.CancelCutoffHours,
            policy.MinimumBookingLeadMinutes, policy.DepositPercent);
    }

    private async Task<BookingPolicy> SaveCategoryAsync(
        Guid tenantId, Guid categoryId, int? days, int? cutoff,
        bool leadSpecified, int? lead, bool depositSpecified, int? deposit,
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
        if (leadSpecified)
        {
            policy.MinimumBookingLeadMinutes = lead;
        }

        if (depositSpecified)
        {
            policy.DepositPercent = deposit;
        }

        policy.CancellationPolicyText = null;
        policy.MaxActiveReservationsPerCustomerPerDay = null;
        policy.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);
        return policy;
    }

    private async Task<BookingPolicyResponseDto> SaveAsync(
        Guid tenantId,
        string? text,
        int? limit,
        JsonOptional<int> horizon,
        JsonOptional<int> cancelCutoff,
        JsonOptional<int> leadMinutes,
        JsonOptional<int> depositPercent,
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
        var resolvedLead = leadMinutes.IsSpecified
            ? BookingPolicy.NormalizeLeadMinutes(leadMinutes.Value)
            : policy?.MinimumBookingLeadMinutes;
        var resolvedDeposit = depositPercent.IsSpecified
            ? BookingPolicy.NormalizeDepositPercent(depositPercent.Value)
            : policy?.DepositPercent;
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
                MinimumBookingLeadMinutes = resolvedLead,
                DepositPercent = resolvedDeposit,
            };
            dbContext.BookingPolicies.Add(policy);
        }
        else
        {
            policy.Replace(text, limit, resolvedHorizon);
            policy.CancelCutoffHours = resolvedCutoff;
            policy.MinimumBookingLeadMinutes = resolvedLead;
            policy.DepositPercent = resolvedDeposit;
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
            policy?.CancelCutoffHours,
            policy?.MinimumBookingLeadMinutes,
            policy?.DepositPercent);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation;
}
