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
            .Where(policy => policy.TenantId == tenantId && policy.ResourceType == null);
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
