using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Services.Tenancy;

/// <summary>
/// Default customer-portal menu item created when a module is enabled for a tenant.
/// Idempotent: a module that already has any menu item (stored or pending) is skipped.
/// </summary>
public static class ModuleDefaultMenuItems
{
    public sealed record Default(string Label, int SortOrder);

    public static readonly IReadOnlyDictionary<string, Default> ByModule =
        new Dictionary<string, Default>(StringComparer.OrdinalIgnoreCase)
        {
            [PlatformModules.Rentals] = new("Reservar horário", 0),
            [PlatformModules.Catalog] = new("Catálogo", 1),
        };

    /// <param name="existingMenuModules">Modules that already have a stored menu item for this tenant.</param>
    public static void Ensure(
        AppDbContext dbContext,
        Guid tenantId,
        IEnumerable<string> enabledModules,
        IEnumerable<string> existingMenuModules)
    {
        var pending = dbContext.ChangeTracker.Entries<TenantModuleMenuItem>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.TenantId == tenantId)
            .Select(entry => entry.Entity.ModuleName);
        var existing = new HashSet<string>(existingMenuModules.Concat(pending), StringComparer.OrdinalIgnoreCase);

        foreach (var module in enabledModules.Where(ByModule.ContainsKey))
        {
            if (!existing.Add(module))
            {
                continue;
            }

            var item = ByModule[module];
            dbContext.TenantModuleMenuItems.Add(
                new TenantModuleMenuItem(tenantId, module, item.Label, item.SortOrder));
        }
    }
}
