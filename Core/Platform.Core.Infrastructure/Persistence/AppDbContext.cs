using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Common;
using Platform.Core.Domain.Entities;

namespace Platform.Core.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    private readonly ITenantProvider _tenantProvider;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantProvider tenantProvider)
        : base(options)
    {
        _tenantProvider = tenantProvider;
    }

    // --- Schema: core ---
    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantModule> TenantModules => Set<TenantModule>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Unit> Units => Set<Unit>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<UserRole> UserRoles => Set<UserRole>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    // --- Schema: assets (inventory module — full model; schema rename to inventory is deferred) ---
    public DbSet<AssetCategory> AssetCategories => Set<AssetCategory>();

    public DbSet<Asset> Assets => Set<Asset>();

    // --- Schema: pmoc / os (maintenance module — full model; schema rename to maintenance is deferred) ---
    public DbSet<MaintenancePlan> MaintenancePlans => Set<MaintenancePlan>();

    public DbSet<PlanTask> PlanTasks => Set<PlanTask>();

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();

    public DbSet<WorkOrderTask> WorkOrderTasks => Set<WorkOrderTask>();

    public DbSet<GlobalMaintenanceTemplate> GlobalMaintenanceTemplates =>
        Set<GlobalMaintenanceTemplate>();

    public DbSet<GlobalTemplateTask> GlobalTemplateTasks => Set<GlobalTemplateTask>();

    // --- Schema: rentals (isolated; no FKs to inventory/maintenance) ---
    public DbSet<RentalAsset> RentalAssets => Set<RentalAsset>();

    public DbSet<RentalPricing> RentalPricings => Set<RentalPricing>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<ReservationItem> ReservationItems => Set<ReservationItem>();

    private Guid? CurrentTenantId => _tenantProvider.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("core");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        ApplyTenantQueryFilters(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }

    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var method = typeof(AppDbContext)
                .GetMethod(nameof(ApplyTenantQueryFilter), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(entityType.ClrType);

            method.Invoke(this, [modelBuilder]);
        }
    }

    private void ApplyTenantQueryFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(CreateTenantFilter<TEntity>());
    }

    private Expression<Func<TEntity, bool>> CreateTenantFilter<TEntity>()
        where TEntity : class, ITenantScoped
    {
        return entity => CurrentTenantId == null || entity.TenantId == CurrentTenantId;
    }
}
