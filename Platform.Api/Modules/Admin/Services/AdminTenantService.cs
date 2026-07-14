using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Admin.Services;

public sealed class AdminTenantService(AppDbContext dbContext) : IAdminTenantService
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

        if (!string.IsNullOrWhiteSpace(request.LogoUrl)
            && !Uri.TryCreate(request.LogoUrl.Trim(), UriKind.Absolute, out _))
        {
            throw new ArgumentException("LogoUrl must be a valid absolute URL.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var tenant = new Tenant(
                legalName,
                taxId,
                tradeName: null,
                subdomain: subdomain,
                logoUrl: request.LogoUrl);

            dbContext.Tenants.Add(tenant);

            foreach (var moduleName in modules)
            {
                dbContext.TenantModules.Add(new TenantModule(tenant.Id, moduleName, isActive: true));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

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
            tenant.LogoUrl,
            tenant.IsActive,
            tenant.CreatedAt,
            activeModules);
    }
}
