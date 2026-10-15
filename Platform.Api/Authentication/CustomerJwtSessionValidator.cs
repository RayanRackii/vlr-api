using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authentication;

public class CustomerJwtSessionValidator
{
    private readonly Func<Guid, Guid, CancellationToken, Task<CustomerPasswordStampRow?>> _lookup;
    private readonly ILogger<CustomerJwtSessionValidator> _logger;

    public CustomerJwtSessionValidator(
        AppDbContext dbContext,
        ILogger<CustomerJwtSessionValidator> logger)
        : this(CreateStoreLookup(dbContext), logger)
    {
    }

    /// <summary>
    /// Test seam for store failures. Production DI uses the <see cref="AppDbContext"/> constructor.
    /// </summary>
    internal CustomerJwtSessionValidator(
        Func<Guid, Guid, CancellationToken, Task<CustomerPasswordStampRow?>> lookup,
        ILogger<CustomerJwtSessionValidator> logger)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // context.Fail is HTTP 401. A store outage must not look like an expired session.
            // The replacement exception is what JwtBearer and the pipeline logger observe.
            LogStoreFailure(_logger, exception);
            throw new CustomerSessionStoreUnavailableException();
        }
    }

    public async Task<bool> IsCurrentAsync(
        ClaimsPrincipal? principal,
        CancellationToken cancellationToken)
    {
        var customerIdRaw = principal?.FindFirst(CustomerClaimTypes.CustomerId)?.Value;
        var tenantIdRaw = principal?.FindFirst(CustomerClaimTypes.TenantId)?.Value;
        if (!Guid.TryParse(customerIdRaw, out var customerId)
            || !Guid.TryParse(tenantIdRaw, out var tenantId))
        {
            return false;
        }

        var stamp = await _lookup(customerId, tenantId, cancellationToken);

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

    // Session validation runs before the ambient tenant is bound. The global filter is
    // off when that tenant is null, and when it is set it would hide a customer whose
    // JWT tenant differs from the ambient value. IgnoreQueryFilters keeps this predicate
    // as the only tenant constraint. Both ids come from the customer JWT, not from ambient state.
    public static IQueryable<CustomerPasswordStampRow> Lookup(
        AppDbContext dbContext,
        Guid customerId,
        Guid tenantId) =>
        dbContext.Customers
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(customer => customer.Id == customerId && customer.TenantId == tenantId)
            .Select(customer => new CustomerPasswordStampRow(customer.PasswordChangedAt));

    private static Func<Guid, Guid, CancellationToken, Task<CustomerPasswordStampRow?>> CreateStoreLookup(
        AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return (customerId, tenantId, cancellationToken) =>
            Lookup(dbContext, customerId, tenantId).FirstOrDefaultAsync(cancellationToken);
    }

    private static void LogStoreFailure(ILogger logger, Exception exception)
    {
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        var sqlState = TryGetSqlState(exception);
        if (sqlState is null)
        {
            logger.LogError(
                "Customer session stamp check failed. ExceptionType={ExceptionType}",
                exceptionType);
            return;
        }

        logger.LogError(
            "Customer session stamp check failed. ExceptionType={ExceptionType} SqlState={SqlState}",
            exceptionType,
            sqlState);
    }

    private static string? TryGetSqlState(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && IsSqlState(postgres.SqlState))
            {
                return postgres.SqlState;
            }
        }

        return null;
    }

    private static bool IsSqlState(string? value)
    {
        if (value is not { Length: 5 })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'A' and <= 'Z')))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record CustomerPasswordStampRow(DateTimeOffset? PasswordChangedAt);
