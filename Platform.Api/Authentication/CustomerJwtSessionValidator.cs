using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authentication;

public class CustomerJwtSessionValidator(
    AppDbContext dbContext,
    ILogger<CustomerJwtSessionValidator> logger)
{
    public virtual async Task ValidateAsync(TokenValidatedContext context)
    {
        try
        {
            var current = await IsCurrentAsync(
                context.Principal,
                context.HttpContext.RequestAborted);
            if (!current)
            {
                context.Fail("Session expired.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Customer session stamp check failed.");
            context.Fail("Session expired.");
        }
    }

    public async Task<bool> IsCurrentAsync(
        System.Security.Claims.ClaimsPrincipal? principal,
        CancellationToken cancellationToken)
    {
        var customerIdRaw = principal?.FindFirst(CustomerClaimTypes.CustomerId)?.Value;
        var tenantIdRaw = principal?.FindFirst(CustomerClaimTypes.TenantId)?.Value;
        if (!Guid.TryParse(customerIdRaw, out var customerId)
            || !Guid.TryParse(tenantIdRaw, out var tenantId))
        {
            return false;
        }

        var stamp = await Lookup(dbContext, customerId, tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (stamp is null)
        {
            return false;
        }

        if (stamp.PasswordChangedAt is null)
        {
            return true;
        }

        var claim = principal?.FindFirst(CustomerPasswordStamp.ClaimType)?.Value;
        return CustomerPasswordStamp.Matches(claim, stamp.PasswordChangedAt.Value);
    }

    public static IQueryable<CustomerPasswordStampRow> Lookup(
        AppDbContext dbContext,
        Guid customerId,
        Guid tenantId) =>
        dbContext.Customers
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(customer => customer.Id == customerId && customer.TenantId == tenantId)
            .Select(customer => new CustomerPasswordStampRow(customer.PasswordChangedAt));
}

public sealed record CustomerPasswordStampRow(DateTimeOffset? PasswordChangedAt);
