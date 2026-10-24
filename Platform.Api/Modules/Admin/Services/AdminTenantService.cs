using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Services.Svg;
using Platform.Api.Storage;
using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Admin.Services;

public sealed class AdminTenantService(
    AppDbContext dbContext,
    ITenantUserAdminService tenantUserAdminService,
    IPlatformAdminMembershipService platformAdminMembershipService,
    ITenantAccessBootstrapper tenantAccessBootstrapper,
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    IOptions<PlatformAdminOptions> platformAdminOptions,
    ILogger<AdminTenantService> logger,
    IOptions<StorageOptions> storageOptions,
    IStorageProvider storageProvider) : IAdminTenantService
{
    public async Task<IReadOnlyList<TenantAdminResponseDto>> ListAsync(
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-list");
        var tenants = await TenantsIncludingModules()
            .AsNoTracking()
            .OrderBy(t => t.LegalName)
            .ToListAsync(cancellationToken);

        var tenantIds = tenants.Select(t => t.Id).ToList();
        var familyKeysByTenant = await LoadFamilyKeysByTenantAsync(tenantIds, cancellationToken);

        return tenants
            .Select(t => ToResponse(
                t,
                familyKeysByTenant.GetValueOrDefault(t.Id) ?? []))
            .ToList();
    }

    public async Task<TenantAdminResponseDto?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-get");
        var tenant = await TenantsIncludingModules()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (tenant is null)
        {
            return null;
        }

        var familyKeys = await LoadFamilyKeysAsync(tenant.Id, cancellationToken);
        return ToResponse(tenant, familyKeys);
    }

    public async Task<TenantAdminResponseDto> CreateAsync(
        CreateTenantRequestDto request,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-create");
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

        if (ReservedTenantSubdomains.IsReserved(subdomain))
        {
            throw new ArgumentException(ReservedTenantSubdomains.ReservedMessage);
        }

        var modules = PlatformModuleCatalog.NormalizeEntitlements(request.ActiveModules);

        if (modules.Count == 0)
        {
            throw new ArgumentException("At least one active module is required.");
        }

        var families = await ResolveFamiliesAsync(request.AssetFamilyKeys, cancellationToken);
        EnsurePmocHasProvisioningFamily(modules, families.Select(f => f.Key));

        var logoSvg = SvgMarkupValidator.Normalize(request.LogoSvg);

        ValidateBrandingFields(
            request.PrimaryColor,
            request.AccentColor,
            request.WelcomeTagline);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

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
            dbContext.BookingPolicies.Add(BookingPolicyDefaults.SuggestedFor(tenant.Id));

            dbContext.Units.Add(new Unit(tenant.Id, "Matriz", "HQ"));

            foreach (var moduleName in modules)
            {
                dbContext.TenantModules.Add(new TenantModule(tenant.Id, moduleName, isActive: true));
            }

            foreach (var family in families)
            {
                dbContext.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, family.Id));
            }

            await AssetCategoryExampleSeeder.SeedForFamilyIdsAsync(
                dbContext,
                tenant.Id,
                families.Select(f => f.Id).ToList(),
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            await tenantAccessBootstrapper.EnsureAsync(tenant.Id, cancellationToken);

            await platformAdminMembershipService.ProvisionPlatformAdminsAsync(
                tenant.Id,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(request.AdminEmail))
            {
                var adminEmail = request.AdminEmail.Trim().ToLowerInvariant();
                var alreadyMember = await dbContext.Users
                    .IgnoreQueryFilters()
                    .AnyAsync(
                        u => u.TenantId == tenant.Id && u.Email == adminEmail,
                        cancellationToken);

                if (!alreadyMember)
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
            }

            var created = await TenantsIncludingModules()
                .AsNoTracking()
                .FirstAsync(t => t.Id == tenant.Id, cancellationToken);

            var familyKeys = await LoadFamilyKeysAsync(tenant.Id, cancellationToken);
            return ToResponse(created, familyKeys);
        }
        catch (DbUpdateException ex) when (IsTenantIdentityUniqueViolation(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw new InvalidOperationException(
                "A tenant with the same TaxId or Subdomain already exists.",
                ex);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<TenantAdminResponseDto> UpdateAsync(
        Guid id,
        UpdateTenantRequestDto request,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-update");
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

        var families = await ResolveFamiliesAsync(request.AssetFamilyKeys, cancellationToken);

        var logoSvg = SvgMarkupValidator.Normalize(request.LogoSvg);

        ValidateBrandingFields(
            request.PrimaryColor,
            request.AccentColor,
            request.WelcomeTagline);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var tenant = await TenantsIncludingModules()
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

            if (tenant is null)
            {
                throw new KeyNotFoundException("Tenant not found.");
            }

            if (ReservedTenantSubdomains.IsReserved(subdomain)
                && !string.Equals(tenant.Subdomain, subdomain, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(ReservedTenantSubdomains.ReservedMessage);
            }

            await EnsureTaxIdAndSubdomainAvailableAsync(id, taxId, subdomain, cancellationToken);

            var existingActive = tenant.Modules
                .Where(m => m.IsActive)
                .Select(m => m.ModuleName)
                .ToList();

            var modules = PlatformModuleCatalog.NormalizeEntitlements(
                request.ActiveModules,
                existingActive);

            if (modules.Count == 0)
            {
                throw new ArgumentException("At least one active module is required.");
            }

            EnsurePmocHasProvisioningFamily(modules, families.Select(f => f.Key));

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
            await SyncTenantAssetFamiliesAsync(tenant.Id, families, cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await tenantAccessBootstrapper.EnsureAsync(id, cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            var updated = await TenantsIncludingModules()
                .AsNoTracking()
                .FirstAsync(t => t.Id == id, cancellationToken);

            var familyKeys = await LoadFamilyKeysAsync(id, cancellationToken);
            return ToResponse(updated, familyKeys);
        }
        catch (DbUpdateException ex) when (IsTenantIdentityUniqueViolation(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw new InvalidOperationException(
                "A tenant with the same TaxId or Subdomain already exists.",
                ex);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-delete");
        List<string> supabaseAuthIds;

        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                if (dbContext.Database.IsNpgsql())
                    await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT id FROM core.tenants WHERE id = {id} FOR UPDATE", cancellationToken);
                var tenantExists = await dbContext.Tenants
                    .AnyAsync(t => t.Id == id, cancellationToken);

                if (!tenantExists)
                {
                    throw new KeyNotFoundException("Tenant not found.");
                }

                // Users are tenant-scoped. After enter, the ambient filter would hide the
                // target tenant's memberships and any other tenant that still shares an auth id.
                var tenantUsers = await dbContext.Users
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(u => u.TenantId == id)
                    .Select(u => new { u.SupabaseAuthId, u.Email })
                    .ToListAsync(cancellationToken);

                var candidateAuthIds = tenantUsers
                    .Select(u => u.SupabaseAuthId)
                    .Where(authId => !string.IsNullOrWhiteSpace(authId))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var platformAdminEmails = platformAdminOptions.Value.Emails
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .Select(e => e.Trim().ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var authIdsStillUsedElsewhere = candidateAuthIds.Count == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : (await dbContext.Users
                        .AsNoTracking()
                        .IgnoreQueryFilters()
                        .Where(u =>
                            u.TenantId != id
                            && candidateAuthIds.Contains(u.SupabaseAuthId))
                        .Select(u => u.SupabaseAuthId)
                        .Distinct()
                        .ToListAsync(cancellationToken))
                        .ToHashSet(StringComparer.Ordinal);

                var platformAdminAuthIds = tenantUsers
                    .Where(u => platformAdminEmails.Contains(u.Email.Trim().ToLowerInvariant()))
                    .Select(u => u.SupabaseAuthId)
                    .ToHashSet(StringComparer.Ordinal);

                supabaseAuthIds = candidateAuthIds
                    .Where(authId =>
                        !authIdsStillUsedElsewhere.Contains(authId)
                        && !platformAdminAuthIds.Contains(authId))
                    .ToList();

                // Ambient filter would hide this tenant's rows when the Super-Admin JWT
                // still carries another tenant id. Restrict FKs then block the tenant
                // delete; tables without a tenant FK would be left behind. Scope stays
                // TenantId == id.
                await DeleteForTenantAsync(dbContext.ReservationQueueTickets, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.ReservationQueueSessions, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.Slots, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.ReservationItems, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.Reservations, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.ScheduleTemplates, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.LayoutDecorations, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.BookingMapSettings, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.RentalLayoutItems, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.RentalLayouts, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.RentalPricings, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.OccupancyKinds, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.RentalAssets, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.BookingPolicies, id, cancellationToken);

                await DeleteForTenantAsync(dbContext.WorkOrderTaskFiles, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.WorkOrderTasks, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.WorkOrders, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.PlanTasks, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.MaintenancePlans, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.Assets, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.TenantAssetFamilies, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.AssetCategories, id, cancellationToken);

                // Catalog orders and product requests restrict the customer
                // (ON DELETE RESTRICT). Remove that graph for this tenant before
                // the customer row. Order items also restrict the product.
                await DeleteForTenantAsync(dbContext.CatalogOrderItems, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.CatalogOrderStatusHistories, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.CatalogOrders, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.CatalogProductCategories, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.CatalogProductTags, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.CatalogProductFiles, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.CatalogProducts, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.TaxonomyTags, id, cancellationToken);
                for (var level = 0; level < 3; level++)
                {
                    await dbContext.TaxonomyCategories.IgnoreQueryFilters()
                        .Where(category => category.TenantId == id
                            && !dbContext.TaxonomyCategories.IgnoreQueryFilters()
                                .Any(child => child.TenantId == id && child.ParentId == category.Id))
                        .ExecuteDeleteAsync(cancellationToken);
                }
                await DeleteForTenantAsync(dbContext.CatalogOrderNumberSequences, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.ProductRequestFiles, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.ProductRequests, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.CatalogPolicies, id, cancellationToken);

                // Attempts have no TenantId. Scope them through this tenant's
                // deliveries before the delivery row is removed. Notification
                // templates are global and are not deleted.
                await DeleteDeliveryAttemptsForTenantAsync(id, cancellationToken);
                await DeleteForTenantAsync(dbContext.NotificationDeliveries, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.Notifications, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.TenantNotificationChannelConfigs, id, cancellationToken);

                // TrialSignupClaim is not ITenantScoped: TenantId is optional and
                // there is no tenant FK. Unlinked claims and other tenants stay.
                await dbContext.TrialSignupClaims
                    .IgnoreQueryFilters()
                    .Where(claim => claim.TenantId == id)
                    .ExecuteDeleteAsync(cancellationToken);

                await DeleteForTenantAsync(dbContext.OtpCodes, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.Customers, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.TenantResourceSegments, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.TenantModuleMenuItems, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.TenantRegistrationFields, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.UserInvites, id, cancellationToken);

                // UserRoles cascade from users; RolePermissions cascade from roles.
                await DeleteForTenantAsync(dbContext.Users, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.Roles, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.Units, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.TenantModules, id, cancellationToken);
                await DeleteForTenantAsync(dbContext.TenantSettings, id, cancellationToken);

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

        var tenantStoragePrefix = $"{id:N}/";
        foreach (var bucket in new[]
                 {
                     storageOptions.Value.PublicBucket,
                     storageOptions.Value.PrivateBucket,
                 }.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await storageProvider.DeletePrefixAsync(bucket, tenantStoragePrefix, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Tenant storage purge failed after database deletion. Bucket: {Bucket}; Prefix: {Prefix}",
                    bucket,
                    tenantStoragePrefix);
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

    public async Task<EnterTenantEnvironmentResponseDto> EnterEnvironmentAsync(
        Guid tenantId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-enter-environment");
        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.IsActive, cancellationToken)
            ?? throw new KeyNotFoundException("Tenant not found or inactive.");

        await platformAdminMembershipService.EnterTenantAsync(
            tenantId,
            principal,
            cancellationToken);

        return new EnterTenantEnvironmentResponseDto(tenant.Id, tenant.LegalName);
    }

    public Task ExitEnvironmentAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) =>
        platformAdminMembershipService.ExitTenantAsync(principal, cancellationToken);

    private void SyncTenantModules(Tenant tenant, IReadOnlyList<string> desiredModules)
    {
        var desired = new HashSet<string>(desiredModules, StringComparer.OrdinalIgnoreCase);
        var remainingDesired = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);

        foreach (var module in tenant.Modules.ToList())
        {
            if (desired.Contains(module.ModuleName))
            {
                remainingDesired.Remove(module.ModuleName);

                if (!module.IsActive)
                {
                    module.Activate();
                }

                continue;
            }

            if (!PlatformModuleCatalog.ShouldRemoveStoredModule(module.ModuleName, desired))
            {
                continue;
            }

            dbContext.TenantModules.Remove(module);
        }

        foreach (var moduleName in remainingDesired)
        {
            if (PlatformModuleCatalog.IsLegacy(moduleName))
            {
                continue;
            }

            dbContext.TenantModules.Add(new TenantModule(tenant.Id, moduleName, isActive: true));
        }
    }

    private async Task SyncTenantAssetFamiliesAsync(
        Guid tenantId,
        IReadOnlyList<ResolvedAssetFamily> desiredFamilies,
        CancellationToken cancellationToken)
    {
        // Ambient filter would hide this tenant's families when the JWT points elsewhere
        // and the update would insert duplicates. Still limited to tenantId.
        var existing = await dbContext.TenantAssetFamilies
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var desired = desiredFamilies.Select(f => f.Id).ToHashSet();

        foreach (var row in existing)
        {
            if (!desired.Contains(row.FamilyId))
            {
                dbContext.TenantAssetFamilies.Remove(row);
            }
        }

        var existingIds = existing.Select(e => e.FamilyId).ToHashSet();
        var newlyAddedIds = new List<Guid>();
        foreach (var family in desiredFamilies)
        {
            if (existingIds.Contains(family.Id))
            {
                continue;
            }

            dbContext.TenantAssetFamilies.Add(new TenantAssetFamily(tenantId, family.Id));
            newlyAddedIds.Add(family.Id);
        }

        await AssetCategoryExampleSeeder.SeedForFamilyIdsAsync(
            dbContext,
            tenantId,
            newlyAddedIds,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ResolvedAssetFamily>> ResolveFamiliesAsync(
        IReadOnlyList<string>? assetFamilyKeys,
        CancellationToken cancellationToken)
    {
        var normalized = (assetFamilyKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Rolling deploy: older FE omit this field — keep tenants creatable.
        if (normalized.Count == 0)
        {
            normalized.Add(AssetFamilyKeys.Generic);
        }

        var families = await dbContext.AssetFamilies
            .AsNoTracking()
            .Where(f => f.IsActive && normalized.Contains(f.Key))
            .ToListAsync(cancellationToken);

        if (families.Count != normalized.Count)
        {
            var found = families.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
            var missing = normalized.Where(k => !found.Contains(k));
            throw new ArgumentException(
                $"Unknown or inactive asset family: {string.Join(", ", missing)}. "
                + "Apply the AddAssetFamilies migration if the catalog is empty.");
        }

        return families
            .OrderBy(f => f.SortOrder)
            .Select(f => new ResolvedAssetFamily(f.Id, f.Key))
            .ToList();
    }

    private static void EnsurePmocHasProvisioningFamily(
        IReadOnlyList<string> modules,
        IEnumerable<string> familyKeys)
    {
        var pmocOn = modules.Any(module =>
            string.Equals(module, PlatformModules.Pmoc, StringComparison.OrdinalIgnoreCase));

        if (!pmocOn)
        {
            return;
        }

        if (!AssetCategoryExampleSeeds.HasPmocProvisioningFamily(familyKeys))
        {
            throw new ArgumentException(AssetCategoryExampleSeeds.PmocRequiresProvisioningFamilyMessage);
        }
    }

    private sealed record ResolvedAssetFamily(Guid Id, string Key);

    private async Task<IReadOnlyList<string>> LoadFamilyKeysAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        return await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Join(
                dbContext.AssetFamilies.AsNoTracking(),
                t => t.FamilyId,
                f => f.Id,
                (_, f) => f)
            .OrderBy(f => f.SortOrder)
            .Select(f => f.Key)
            .ToListAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadFamilyKeysByTenantAsync(
        IReadOnlyList<Guid> tenantIds,
        CancellationToken cancellationToken)
    {
        if (tenantIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        var rows = await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(t => tenantIds.Contains(t.TenantId))
            .Join(
                dbContext.AssetFamilies.AsNoTracking(),
                t => t.FamilyId,
                f => f.Id,
                (t, f) => new { t.TenantId, f.Key, f.SortOrder })
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.TenantId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Key).ToList());
    }

    /// <summary>
    /// Platform-admin tenant graph. <see cref="TenantModule"/> is tenant-scoped, so the
    /// ambient filter would hide modules whenever the Super-Admin JWT still carries
    /// another tenant id. Callers keep an explicit tenant predicate (or list every
    /// tenant, which this policy is allowed to do). Other tenants' rows stay on their
    /// own navigations.
    /// </summary>
    private IQueryable<Tenant> TenantsIncludingModules() =>
        dbContext.Tenants.IgnoreQueryFilters().Include(t => t.Modules);

    private async Task EnsureTaxIdAndSubdomainAvailableAsync(
        Guid exceptTenantId,
        string taxId,
        string subdomain,
        CancellationToken cancellationToken)
    {
        var taken = await dbContext.Tenants
            .AsNoTracking()
            .AnyAsync(
                t => t.Id != exceptTenantId
                    && (t.TaxId == taxId || t.Subdomain == subdomain),
                cancellationToken);

        if (taken)
        {
            throw new InvalidOperationException(
                "A tenant with the same TaxId or Subdomain already exists.");
        }
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

    /// <summary>
    /// Delivery attempts are not tenant-scoped. The predicate is still this tenant:
    /// only attempts whose delivery has <paramref name="tenantId"/>.
    /// </summary>
    private Task<int> DeleteDeliveryAttemptsForTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var deliveryIds = dbContext.NotificationDeliveries
            .IgnoreQueryFilters()
            .Where(delivery => delivery.TenantId == tenantId)
            .Select(delivery => delivery.Id);

        return dbContext.NotificationDeliveryAttempts
            .IgnoreQueryFilters()
            .Where(attempt => deliveryIds.Contains(attempt.DeliveryId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes rows for <paramref name="tenantId"/> only. IgnoreQueryFilters is required
    /// because the global filter is the ambient tenant, not the tenant being deleted.
    /// </summary>
    private static Task<int> DeleteForTenantAsync<TEntity>(
        DbSet<TEntity> set,
        Guid tenantId,
        CancellationToken cancellationToken)
        where TEntity : class, ITenantScoped =>
        set.IgnoreQueryFilters()
            .Where(entity => entity.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

    private static bool IsTenantIdentityUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException postgresException
            && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
            && postgresException.ConstraintName is "ix_tenants_tax_id" or "ix_tenants_subdomain";
    }

    private static TenantAdminResponseDto ToResponse(
        Tenant tenant,
        IReadOnlyList<string> assetFamilyKeys)
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
            activeModules,
            assetFamilyKeys);
    }
}
