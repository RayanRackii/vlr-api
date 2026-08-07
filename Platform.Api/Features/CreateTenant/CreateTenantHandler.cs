using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Services.Brazil;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Services;
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

    private static readonly string[] TrialModules =
    [
        PlatformModules.Inventory,
        PlatformModules.Maintenance,
        PlatformModules.Pmoc,
        PlatformModules.WorkOrders,
        PlatformModules.Rentals,
    ];

    private static readonly string[] TrialFamilyKeys =
    [
        AssetFamilyKeys.Spaces,
        AssetFamilyKeys.Electrical,
        AssetFamilyKeys.Goods,
        AssetFamilyKeys.Generic,
    ];

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
        TrialSignupClaim? trialClaim = null;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            string? subdomain = null;

            if (request.IsTrial)
            {
                var emailNormalized = request.AdminEmail.Trim().ToLowerInvariant();
                var phoneNormalized = BrazilianDocumentValidator.NormalizePhoneBr(request.AdminPhone);

                trialClaim = new TrialSignupClaim(emailNormalized, phoneNormalized);
                _dbContext.TrialSignupClaims.Add(trialClaim);

                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Results.Conflict(new
                    {
                        error = "Trial already used for this email or phone.",
                    });
                }

                subdomain = await TrialSubdomainGenerator.AllocateAsync(
                    request.LegalName,
                    async candidate => await _dbContext.Tenants
                        .AsNoTracking()
                        .AnyAsync(t => t.Subdomain == candidate, cancellationToken));
            }

            var tenant = new Tenant(
                request.LegalName,
                request.TaxId,
                request.TradeName,
                subdomain: subdomain);

            if (request.IsTrial)
            {
                tenant.ConfigureAsTrial(DateTimeOffset.UtcNow);
            }

            var headquartersUnit = new Unit(
                tenant.Id,
                request.HeadquartersUnitName,
                request.HeadquartersUnitCode);

            _dbContext.Tenants.Add(tenant);
            _dbContext.Units.Add(headquartersUnit);

            if (request.IsTrial)
            {
                foreach (var moduleName in TrialModules)
                {
                    _dbContext.TenantModules.Add(new TenantModule(tenant.Id, moduleName, isActive: true));
                }

                var familyIds = await ResolveTrialFamilyIdsAsync(cancellationToken);
                foreach (var familyId in familyIds)
                {
                    _dbContext.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, familyId));
                }

                SeedTrialExampleCategories(tenant.Id, familyIds);
            }

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

            if (trialClaim is not null)
            {
                trialClaim.LinkTenant(tenant.Id);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var response = new CreateTenantResponse(
                tenant.Id,
                headquartersUnit.Id,
                adminUser.Id,
                superAdminRole.Id,
                supabaseUserId,
                tenant.Subdomain);

            return Results.Created($"/api/tenants/{tenant.Id}", response);
        }
        catch (ArgumentException ex)
        {
            await RollbackWithCompensationAsync(transaction, supabaseUserId, cancellationToken);
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Trial already used", StringComparison.Ordinal))
        {
            await RollbackWithCompensationAsync(transaction, supabaseUserId, cancellationToken);
            return Results.Conflict(new { error = ex.Message });
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

    private async Task<IReadOnlyList<Guid>> ResolveTrialFamilyIdsAsync(CancellationToken cancellationToken)
    {
        var families = await _dbContext.AssetFamilies
            .AsNoTracking()
            .Where(f => f.IsActive && TrialFamilyKeys.Contains(f.Key))
            .OrderBy(f => f.SortOrder)
            .ToListAsync(cancellationToken);

        if (families.Count != TrialFamilyKeys.Length)
        {
            var found = families.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            var missing = TrialFamilyKeys.Where(k => !found.Contains(k));
            throw new InvalidOperationException(
                $"Unknown or inactive asset family: {string.Join(", ", missing)}. "
                + "Apply the AddAssetFamilies migration if the catalog is empty.");
        }

        return families.Select(f => f.Id).ToList();
    }

    private void SeedTrialExampleCategories(Guid tenantId, IReadOnlyList<Guid> familyIds)
    {
        var seeds = new List<(Guid FamilyId, string Name)>
        {
            (AssetFamilyKeys.Ids.Spaces, "Quadra"),
            (AssetFamilyKeys.Ids.Electrical, "Quadro elétrico"),
            (AssetFamilyKeys.Ids.Goods, "Caçamba"),
        };

        foreach (var (familyId, name) in seeds)
        {
            if (!familyIds.Contains(familyId))
            {
                continue;
            }

            _dbContext.AssetCategories.Add(new AssetCategory
            {
                TenantId = tenantId,
                Name = name,
                Description = null,
                Manufacturer = null,
            });
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

        if (request.IsTrial && string.IsNullOrWhiteSpace(request.AdminPhone))
        {
            return "AdminPhone is required for trial signup.";
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
