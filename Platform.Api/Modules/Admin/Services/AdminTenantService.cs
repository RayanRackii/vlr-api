using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Services.Svg;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Admin.Services;

public sealed class AdminTenantService(
    AppDbContext dbContext,
    ITenantUserAdminService tenantUserAdminService,
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    ILogger<AdminTenantService> logger) : IAdminTenantService
{
    public async Task<IReadOnlyList<TenantAdminResponseDto>> ListAsync(
        CancellationToken cancellationToken)
    {
        var tenants = await dbContext.Tenants
            .AsNoTracking()
            .Include(t => t.Modules)
            .OrderBy(t => t.LegalName)
            .ToListAsync(cancellationToken);

        return tenants.Select(ToResponse).ToList();
    }

    public async Task<TenantAdminResponseDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .Include(t => t.Modules)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        return tenant is null ? null : ToResponse(tenant);
    }

    public async Task<TenantAdminResponseDto> CreateAsync(
        CreateTenantRequestDto request,
        CancellationToken cancellationToken)
    {
        var legalName = request.LegalName.Trim();
        var taxId = request.TaxId.Trim();
        var subdomain = request.Subdomain.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ArgumentException("LegalName is required.");
        }

        if (string.IsNullOrWhiteSpace(taxId))
        {
            throw new ArgumentException("TaxId is required.");
        }

        if (string.IsNullOrWhiteSpace(subdomain))
        {
            throw new ArgumentException("Subdomain is required.");
        }

        if (!IsValidSubdomain(subdomain))
        {
            throw new ArgumentException(
                "Subdomain must contain only lowercase letters, numbers, and hyphens.");
        }

        var modules = NormalizeModules(request.ActiveModules);

        if (modules.Count == 0)
        {
            throw new ArgumentException("At least one active module is required.");
        }

        var logoSvg = SvgMarkupValidator.Normalize(request.LogoSvg);

        ValidateBrandingFields(
            request.PrimaryColor,
            request.AccentColor,
            request.WelcomeTagline);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var tenant = new Tenant(
                legalName,
                taxId,
                tradeName: null,
                subdomain: subdomain,
                logoSvg: logoSvg,
                primaryColor: request.PrimaryColor,
                accentColor: request.AccentColor,
                welcomeTagline: request.WelcomeTagline);

            dbContext.Tenants.Add(tenant);

            dbContext.Units.Add(new Unit(tenant.Id, "Headquarters", "HQ"));

            foreach (var moduleName in modules)
            {
                dbContext.TenantModules.Add(new TenantModule(tenant.Id, moduleName, isActive: true));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(request.AdminEmail))
            {
                var adminName = string.IsNullOrWhiteSpace(request.AdminFullName)
                    ? request.AdminEmail.Trim()
                    : request.AdminFullName.Trim();

                await tenantUserAdminService.InviteAsync(
                    tenant.Id,
                    new InviteTenantUserRequestDto
                    {
                        FullName = adminName,
                        Email = request.AdminEmail,
                        RoleName = SystemRoles.Admin,
                    },
                    cancellationToken);
            }

            var created = await dbContext.Tenants
                .AsNoTracking()
                .Include(t => t.Modules)
                .FirstAsync(t => t.Id == tenant.Id, cancellationToken);

            return ToResponse(created);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                "A tenant with the same TaxId or Subdomain already exists.",
                ex);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<TenantAdminResponseDto> UpdateAsync(
        Guid id,
        UpdateTenantRequestDto request,
        CancellationToken cancellationToken)
    {
        var legalName = request.LegalName.Trim();
        var taxId = request.TaxId.Trim();
        var subdomain = request.Subdomain.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(legalName))
        {
            throw new ArgumentException("LegalName is required.");
        }

        if (string.IsNullOrWhiteSpace(taxId))
        {
            throw new ArgumentException("TaxId is required.");
        }

        if (string.IsNullOrWhiteSpace(subdomain))
        {
            throw new ArgumentException("Subdomain is required.");
        }

        if (!IsValidSubdomain(subdomain))
        {
            throw new ArgumentException(
                "Subdomain must contain only lowercase letters, numbers, and hyphens.");
        }

        var modules = NormalizeModules(request.ActiveModules);

        if (modules.Count == 0)
        {
            throw new ArgumentException("At least one active module is required.");
        }

        var logoSvg = SvgMarkupValidator.Normalize(request.LogoSvg);

        ValidateBrandingFields(
            request.PrimaryColor,
            request.AccentColor,
            request.WelcomeTagline);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var tenant = await dbContext.Tenants
                .Include(t => t.Modules)
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (tenant is null)
            {
                throw new KeyNotFoundException("Tenant not found.");
            }

            tenant.UpdateProfile(
                legalName,
                taxId,
                tradeName: null,
                subdomain: subdomain,
                logoSvg: logoSvg,
                primaryColor: request.PrimaryColor,
                accentColor: request.AccentColor,
                welcomeTagline: request.WelcomeTagline);

            SyncTenantModules(tenant, modules);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var updated = await dbContext.Tenants
                .AsNoTracking()
                .Include(t => t.Modules)
                .FirstAsync(t => t.Id == id, cancellationToken);

            return ToResponse(updated);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                "A tenant with the same TaxId or Subdomain already exists.",
                ex);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        List<string> supabaseAuthIds;

        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                var tenantExists = await dbContext.Tenants
                    .AnyAsync(t => t.Id == id, cancellationToken);

                if (!tenantExists)
                {
                    throw new KeyNotFoundException("Tenant not found.");
                }

                supabaseAuthIds = await dbContext.Users
                    .AsNoTracking()
                    .Where(u => u.TenantId == id)
                    .Select(u => u.SupabaseAuthId)
                    .ToListAsync(cancellationToken);

                // Rentals / scheduling (children before parents with Restrict FKs)
                await dbContext.Slots
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.ReservationItems
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.Reservations
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.ScheduleTemplates
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.RentalLayoutItems
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.RentalLayouts
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.RentalPricings
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.OccupancyKinds
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.RentalAssets
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);

                // Work orders / PMOC / inventory
                await dbContext.WorkOrderTasks
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.WorkOrders
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.PlanTasks
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.MaintenancePlans
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.Assets
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.AssetCategories
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);

                // B2C portal
                await dbContext.OtpCodes
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.Customers
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.TenantModuleMenuItems
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.TenantRegistrationFields
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.UserInvites
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);

                // Identity (UserRoles cascade from users; RolePermissions cascade from roles)
                await dbContext.Users
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.Roles
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.Units
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);
                await dbContext.TenantModules
                    .Where(x => x.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);

                await dbContext.Tenants
                    .Where(t => t.Id == id)
                    .ExecuteDeleteAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "Cannot delete this tenant because it still has linked data.",
                    ex);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        foreach (var authId in supabaseAuthIds)
        {
            try
            {
                await supabaseAuthAdminClient.DeleteUserAsync(authId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Tenant {TenantId} deleted, but Supabase auth delete failed for {SupabaseAuthId}.",
                    id,
                    authId);
            }
        }
    }

    private void SyncTenantModules(Tenant tenant, IReadOnlyList<string> desiredModules)
    {
        var remainingDesired = new HashSet<string>(desiredModules, StringComparer.OrdinalIgnoreCase);

        foreach (var module in tenant.Modules.ToList())
        {
            if (remainingDesired.Contains(module.ModuleName))
            {
                remainingDesired.Remove(module.ModuleName);

                if (!module.IsActive)
                {
                    module.Activate();
                }

                continue;
            }

            dbContext.TenantModules.Remove(module);
        }

        foreach (var moduleName in remainingDesired)
        {
            dbContext.TenantModules.Add(new TenantModule(tenant.Id, moduleName, isActive: true));
        }
    }

    private static IReadOnlyList<string> NormalizeModules(IReadOnlyList<string>? activeModules)
    {
        if (activeModules is null || activeModules.Count == 0)
        {
            return [];
        }

        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in activeModules)
        {
            if (!PlatformModules.TryNormalize(module, out var canonical))
            {
                throw new ArgumentException($"Unknown module '{module}'.");
            }

            normalized.Add(canonical);
        }

        return normalized.OrderBy(m => m, StringComparer.Ordinal).ToList();
    }

    private static bool IsValidSubdomain(string subdomain)
    {
        if (subdomain.Length is < 2 or > 63)
        {
            return false;
        }

        if (subdomain.StartsWith('-') || subdomain.EndsWith('-'))
        {
            return false;
        }

        return subdomain.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    /// <summary>
    /// Early validation so bad branding returns ArgumentException → 400
    /// before EF, with the same rules as <see cref="Tenant"/> normalize.
    /// </summary>
    private static void ValidateBrandingFields(
        string? primaryColor,
        string? accentColor,
        string? welcomeTagline)
    {
        ValidateOptionalHexColor(primaryColor, nameof(primaryColor));
        ValidateOptionalHexColor(accentColor, nameof(accentColor));

        if (!string.IsNullOrWhiteSpace(welcomeTagline) && welcomeTagline.Trim().Length > 120)
        {
            throw new ArgumentException("WelcomeTagline must be at most 120 characters.");
        }
    }

    private static void ValidateOptionalHexColor(string? color, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return;
        }

        var trimmed = color.Trim();
        var hex = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;

        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                $"{fieldName} must be a hex color like #1A2B3C.");
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgresException
            && postgresException.SqlState == PostgresErrorCodes.UniqueViolation;
    }

    private static TenantAdminResponseDto ToResponse(Tenant tenant)
    {
        var activeModules = tenant.Modules
            .Where(m => m.IsActive)
            .OrderBy(m => m.ModuleName)
            .Select(m => new TenantModuleResponseDto(m.ModuleName, m.IsActive))
            .ToList();

        return new TenantAdminResponseDto(
            tenant.Id,
            tenant.LegalName,
            tenant.TaxId,
            tenant.Subdomain,
            tenant.LogoSvg,
            tenant.PrimaryColor,
            tenant.AccentColor,
            tenant.WelcomeTagline,
            tenant.IsActive,
            tenant.CreatedAt,
            activeModules);
    }
}
