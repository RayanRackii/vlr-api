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
            return await SaveAsync(tenantId, text, limit, request.MaxAdvanceBookingDays, cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            return await SaveAsync(tenantId, text, limit, request.MaxAdvanceBookingDays, cancellationToken);
        }
    }

    private async Task<BookingPolicyResponseDto> SaveAsync(
        Guid tenantId,
        string? text,
        int? limit,
        JsonOptional<int> horizon,
        CancellationToken cancellationToken)
    {
        var policy = await FindDefaultAsync(tenantId, tracking: true, cancellationToken);
        var resolvedHorizon = horizon.IsSpecified
            ? BookingPolicyText.NormalizeAdvanceDays(horizon.Value)
            : policy?.MaxAdvanceBookingDays;
        if (policy is null)
        {
            policy = new BookingPolicy
            {
                TenantId = tenantId,
                ResourceType = null,
                CancellationPolicyText = text,
                MaxActiveReservationsPerCustomerPerDay = limit,
                MaxAdvanceBookingDays = resolvedHorizon,
            };
            dbContext.BookingPolicies.Add(policy);
        }
        else
        {
            policy.Replace(text, limit, resolvedHorizon);
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
            policy?.MaxAdvanceBookingDays);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation;
}
