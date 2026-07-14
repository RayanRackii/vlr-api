using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Features.CreateTenant;

public interface ICreateTenantHandler
{
    Task<IResult> HandleAsync(CreateTenantRequest request, CancellationToken cancellationToken);
}

public sealed class CreateTenantHandler : ICreateTenantHandler
{
    private const int MinimumPasswordLength = 8;

    private readonly AppDbContext _dbContext;
    private readonly ISupabaseAuthAdminClient _supabaseAuthAdminClient;

    public CreateTenantHandler(
        AppDbContext dbContext,
        ISupabaseAuthAdminClient supabaseAuthAdminClient)
    {
        _dbContext = dbContext;
        _supabaseAuthAdminClient = supabaseAuthAdminClient;
    }

    public async Task<IResult> HandleAsync(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        var validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        string? supabaseUserId = null;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var tenant = new Tenant(request.LegalName, request.TaxId, request.TradeName);
            var headquartersUnit = new Unit(
                tenant.Id,
                request.HeadquartersUnitName,
                request.HeadquartersUnitCode);

            _dbContext.Tenants.Add(tenant);
            _dbContext.Units.Add(headquartersUnit);
            await _dbContext.SaveChangesAsync(cancellationToken);

            supabaseUserId = await _supabaseAuthAdminClient.CreateUserAsync(
                request.AdminEmail,
                request.AdminPassword,
                cancellationToken);

            await _supabaseAuthAdminClient.UpdateUserAppMetadataAsync(
                supabaseUserId,
                tenant.Id,
                cancellationToken);

            var superAdminRole = new Role(
                tenant.Id,
                SystemRoles.SuperAdmin,
                "Tenant super administrator with full access.",
                isSystemRole: true);

            var adminUser = new User(
                tenant.Id,
                supabaseUserId,
                request.AdminFullName,
                request.AdminEmail);

            var userRole = new UserRole(adminUser.Id, superAdminRole.Id);

            _dbContext.Roles.Add(superAdminRole);
            _dbContext.Users.Add(adminUser);
            _dbContext.UserRoles.Add(userRole);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var response = new CreateTenantResponse(
                tenant.Id,
                headquartersUnit.Id,
                adminUser.Id,
                superAdminRole.Id,
                supabaseUserId);

            return Results.Created($"/api/tenants/{tenant.Id}", response);
        }
        catch (SupabaseAuthAdminException ex)
        {
            await RollbackWithCompensationAsync(transaction, supabaseUserId, cancellationToken);

            if (ex.StatusCode is 409 or 422)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            return Results.Json(
                new { error = ex.Message },
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            await RollbackWithCompensationAsync(transaction, supabaseUserId, cancellationToken);
            return Results.Conflict(new { error = "A tenant or user with the same unique identifier already exists." });
        }
        catch
        {
            await RollbackWithCompensationAsync(transaction, supabaseUserId, cancellationToken);
            throw;
        }
    }

    private static string? ValidateRequest(CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LegalName))
        {
            return "LegalName is required.";
        }

        if (string.IsNullOrWhiteSpace(request.TaxId))
        {
            return "TaxId is required.";
        }

        if (string.IsNullOrWhiteSpace(request.HeadquartersUnitName))
        {
            return "HeadquartersUnitName is required.";
        }

        if (string.IsNullOrWhiteSpace(request.AdminFullName))
        {
            return "AdminFullName is required.";
        }

        if (string.IsNullOrWhiteSpace(request.AdminEmail))
        {
            return "AdminEmail is required.";
        }

        if (!IsValidEmail(request.AdminEmail))
        {
            return "AdminEmail is not a valid email address.";
        }

        if (string.IsNullOrWhiteSpace(request.AdminPassword))
        {
            return "AdminPassword is required.";
        }

        if (request.AdminPassword.Length < MinimumPasswordLength)
        {
            return $"AdminPassword must be at least {MinimumPasswordLength} characters long.";
        }

        return null;
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            _ = new MailAddress(email);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgresException
            && postgresException.SqlState == PostgresErrorCodes.UniqueViolation;
    }

    private async Task RollbackWithCompensationAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        string? supabaseUserId,
        CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(supabaseUserId))
        {
            return;
        }

        try
        {
            await _supabaseAuthAdminClient.DeleteUserAsync(supabaseUserId, cancellationToken);
        }
        catch (SupabaseAuthAdminException)
        {
            // Compensation best-effort; the original error remains the primary failure.
        }
    }
}
