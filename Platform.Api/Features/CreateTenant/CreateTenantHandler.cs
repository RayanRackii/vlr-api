using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Authorization;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
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

    internal static readonly string[] TrialModules =
    [
        PlatformModules.Inventory,
        PlatformModules.Pmoc,
        PlatformModules.WorkOrders,
        PlatformModules.Rentals,
    ];

    internal static readonly string[] TrialFamilyKeys =
    [
        AssetFamilyKeys.Spaces,
        AssetFamilyKeys.Electrical,
        AssetFamilyKeys.Goods,
        AssetFamilyKeys.Generic,
    ];

    private readonly AppDbContext _dbContext;
    private readonly ISupabaseAuthAdminClient _supabaseAuthAdminClient;
    private readonly ITenantAccessBootstrapper _tenantAccessBootstrapper;
    private readonly ITenantUserAdminService _tenantUserAdminService;

    public CreateTenantHandler(
        AppDbContext dbContext,
        ISupabaseAuthAdminClient supabaseAuthAdminClient,
        ITenantAccessBootstrapper tenantAccessBootstrapper,
        ITenantUserAdminService tenantUserAdminService)
    {
        _dbContext = dbContext;
        _supabaseAuthAdminClient = supabaseAuthAdminClient;
        _tenantAccessBootstrapper = tenantAccessBootstrapper;
        _tenantUserAdminService = tenantUserAdminService;
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
        Guid? pendingInviteId = null;
        Guid createdTenantId = Guid.Empty;
        Guid headquartersUnitId = Guid.Empty;
        Guid superAdminRoleId = Guid.Empty;
        Guid? adminUserId = null;
        string? subdomain = null;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (request.IsTrial)
            {
                var emailNormalized = request.AdminEmail.Trim().ToLowerInvariant();
                var phoneNormalized = BrazilianDocumentValidator.NormalizePhoneBr(request.AdminPhone);

                var alreadyClaimed = await _dbContext.TrialSignupClaims
                    .AsNoTracking()
                    .AnyAsync(
                        claim => claim.EmailNormalized == emailNormalized
                            || claim.PhoneNormalized == phoneNormalized,
                        cancellationToken);

                if (alreadyClaimed)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Results.Conflict(new
                    {
                        error = "Trial already used for this email or phone.",
                    });
                }

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
                foreach (var moduleName in PlatformModuleCatalog.NormalizeEntitlements(TrialModules))
                {
                    _dbContext.TenantModules.Add(new TenantModule(tenant.Id, moduleName, isActive: true));
                }

                var familyIds = await ResolveTrialFamilyIdsAsync(cancellationToken);
                foreach (var familyId in familyIds)
                {
                    _dbContext.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, familyId));
                }

                await AssetCategoryExampleSeeder.SeedForFamilyIdsAsync(
                    _dbContext,
                    tenant.Id,
                    familyIds,
                    cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var superAdminRole = new Role(
                tenant.Id,
                SystemRoles.SuperAdmin,
                "Tenant super administrator with full access.",
                isSystemRole: true);

            _dbContext.Roles.Add(superAdminRole);

            User? adminUser = null;
            if (!request.IsTrial)
            {
                var adminPassword = request.AdminPassword
                    ?? throw new InvalidOperationException("AdminPassword is required.");

                supabaseUserId = await _supabaseAuthAdminClient.CreateUserAsync(
                    request.AdminEmail,
                    adminPassword,
                    cancellationToken);

                await _supabaseAuthAdminClient.UpdateUserAppMetadataAsync(
                    supabaseUserId,
                    tenant.Id,
                    cancellationToken);

                adminUser = new User(
                    tenant.Id,
                    supabaseUserId,
                    request.AdminFullName,
                    request.AdminEmail);

                _dbContext.Users.Add(adminUser);
                _dbContext.UserRoles.Add(new UserRole(adminUser.Id, superAdminRole.Id));
            }

            if (trialClaim is not null)
            {
                trialClaim.LinkTenant(tenant.Id);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await _tenantAccessBootstrapper.EnsureAsync(tenant.Id, cancellationToken);

            if (request.IsTrial)
            {
                // The invite row shares this transaction. The email is queued only
                // after commit so a rolled-back claim cannot send a live token.
                var invite = await _tenantUserAdminService.InviteAsync(
                    tenant.Id,
                    new InviteTenantUserRequestDto
                    {
                        FullName = request.AdminFullName.Trim(),
                        Email = request.AdminEmail,
                        RoleIds = [superAdminRole.Id],
                    },
                    cancellationToken,
                    enqueueEmail: false);

                pendingInviteId = invite.Id;
            }

            await transaction.CommitAsync(cancellationToken);

            createdTenantId = tenant.Id;
            headquartersUnitId = headquartersUnit.Id;
            superAdminRoleId = superAdminRole.Id;
            adminUserId = adminUser?.Id;
            subdomain = tenant.Subdomain;
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

        if (pendingInviteId is Guid inviteId)
        {
            await _tenantUserAdminService.DeliverInviteEmailAsync(
                createdTenantId,
                inviteId,
                cancellationToken);
        }

        return Results.Created(
            $"/api/tenants/{createdTenantId}",
            new CreateTenantResponse(
                createdTenantId,
                headquartersUnitId,
                adminUserId,
                superAdminRoleId,
                supabaseUserId,
                subdomain,
                pendingInviteId));
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

        if (request.IsTrial)
        {
            if (!string.IsNullOrWhiteSpace(request.AdminPassword))
            {
                return "AdminPassword is not accepted.";
            }

            if (string.IsNullOrWhiteSpace(request.AdminPhone))
            {
                return "AdminPhone is required for trial signup.";
            }
        }
        else if (string.IsNullOrWhiteSpace(request.AdminPassword))
        {
            return "AdminPassword is required.";
        }
        else if (request.AdminPassword.Length < MinimumPasswordLength)
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
