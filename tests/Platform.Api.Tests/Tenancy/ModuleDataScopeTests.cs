using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Platform.Api.Services.Tenancy;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Tenancy;

/// <summary>
/// Model-only coverage of <see cref="ModuleDataScope"/> (module data purge, spec 2.1 / 3.2): every
/// tenant-scoped entity sits in a classified schema, each purgeable module has an acyclic leaf-first
/// order, and the shared registry (assets) and platform (core) schemas are never purgeable.
/// </summary>
public sealed class ModuleDataScopeTests
{
    private static readonly string[] Purgeable =
    [
        PlatformModules.Catalog,
        PlatformModules.WorkOrders,
        PlatformModules.Pmoc,
        PlatformModules.Rentals,
        PlatformModules.ResourcePlanning,
    ];

    public static TheoryData<string> PurgeableModules
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var module in Purgeable)
                data.Add(module);
            return data;
        }
    }

    private static AppDbContext ModelOnlyDb() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only;Username=postgres;Password=postgres")
                .Options,
            new FakeTenantProvider());

    [Fact]
    public void Every_tenant_scoped_entity_lives_in_a_classified_schema()
    {
        using var db = ModelOnlyDb();

        foreach (var entity in TenantPurger.BuildDeletionOrder(db.Model))
        {
            var schema = ModuleDataScope.SchemaOf(entity, db.Model);
            Assert.True(
                ModuleDataScope.IsClassified(schema),
                $"{entity.Name} lives in schema '{schema}', which is not classified in ModuleDataScope. "
                + "Add it to the schema table (purgeable module, or shared) before shipping it.");
        }
    }

    [Fact]
    public void A_schema_outside_the_spec_table_is_not_classified()
    {
        var builder = new ModelBuilder();
        builder.Entity<Future>().ToTable("future_rows", "future").HasKey(row => row.Id);
        builder.Entity<Future>().Property(row => row.TenantId);
        var model = builder.FinalizeModel();
        var entity = Assert.Single(model.GetEntityTypes());

        Assert.Equal("future", ModuleDataScope.SchemaOf(entity, model));
        Assert.False(ModuleDataScope.IsClassified("future"));
        foreach (var schema in new[] { "rentals", "catalog", "os", "pmoc", "planning", "assets", "core", "billing" })
            Assert.True(ModuleDataScope.IsClassified(schema), schema);
    }

    [Fact]
    public void Only_the_five_module_schemas_are_purgeable()
    {
        Assert.Equal(Purgeable, ModuleDataScope.PurgeableModules);
        Assert.Null(ModuleDataScope.ModuleOfSchema("assets"));
        Assert.Null(ModuleDataScope.ModuleOfSchema("core"));
        Assert.Null(ModuleDataScope.ModuleOfSchema("billing"));
        Assert.Equal(PlatformModules.ResourcePlanning, ModuleDataScope.ModuleOfSchema("planning"));
        Assert.Equal(PlatformModules.WorkOrders, ModuleDataScope.ModuleOfSchema("os"));

        using var db = ModelOnlyDb();
        foreach (var notPurgeable in new[] { PlatformModules.Inventory, PlatformModules.Maintenance, "assets", "core", "billing" })
        {
            Assert.False(ModuleDataScope.IsPurgeable(notPurgeable));
            Assert.Throws<ArgumentException>(() => ModuleDataScope.EntitiesFor(db.Model, notPurgeable));
            Assert.Throws<ArgumentException>(() => ModuleDataScope.PlanFor(db.Model, notPurgeable));
        }
    }

    [Theory]
    [MemberData(nameof(PurgeableModules))]
    public void Each_purgeable_module_has_a_non_empty_acyclic_leaf_first_order(string module)
    {
        using var db = ModelOnlyDb();
        var schema = ModuleDataScope.SchemaFor(module);

        var entities = ModuleDataScope.EntitiesFor(db.Model, module);
        Assert.NotEmpty(entities);
        Assert.All(entities, entity => Assert.Equal(schema, ModuleDataScope.SchemaOf(entity, db.Model)));
        AssertLeafFirst(entities);

        var plan = ModuleDataScope.PlanFor(db.Model, module).Select(entry => entry.Entity).ToArray();
        AssertLeafFirst(plan);
        Assert.All(entities, entity => Assert.Contains(entity, plan));
    }

    [Fact]
    public void Every_tenant_entity_of_a_purgeable_schema_is_in_that_modules_scope()
    {
        using var db = ModelOnlyDb();

        foreach (var entity in TenantPurger.BuildDeletionOrder(db.Model))
        {
            var module = ModuleDataScope.ModuleOfSchema(ModuleDataScope.SchemaOf(entity, db.Model));
            if (module is null)
                continue;

            Assert.True(
                ModuleDataScope.EntitiesFor(db.Model, module).Contains(entity),
                $"{entity.Name} is tenant data in the '{module}' schema but is missing from its purge scope.");
        }
    }

    [Fact]
    public void Shared_platform_and_global_entities_are_in_no_module_scope()
    {
        using var db = ModelOnlyDb();
        var everything = Purgeable
            .SelectMany(module => ModuleDataScope.PlanFor(db.Model, module))
            .Select(entry => entry.Entity)
            .Distinct()
            .ToArray();

        Assert.All(everything, entity => Assert.True(
            ModuleDataScope.ModuleOfSchema(ModuleDataScope.SchemaOf(entity, db.Model)) is not null,
            $"{entity.Name} is in a shared schema and must never be purged by a module."));
        foreach (var type in new[]
                 {
                     typeof(Asset), typeof(AssetCategory), typeof(TenantAssetFamily), typeof(Customer),
                     typeof(Unit), typeof(User), typeof(TenantModule), typeof(GlobalMaintenanceTemplate),
                     typeof(GlobalTemplateTask),
                 })
        {
            Assert.DoesNotContain(everything, entity => entity.ClrType == type);
        }
    }

    [Theory]
    [InlineData(PlatformModules.Catalog, nameof(PlanningRowFilter.CatalogOrderSource))]
    [InlineData(PlatformModules.Rentals, nameof(PlanningRowFilter.ReservationBridge))]
    [InlineData(PlatformModules.WorkOrders, nameof(PlanningRowFilter.WorkOrderSource))]
    public void Modules_borrow_only_their_own_planning_allocations(string module, string expected)
    {
        using var db = ModelOnlyDb();

        var plan = ModuleDataScope.PlanFor(db.Model, module);

        var borrowed = Assert.Single(plan, entry => entry.Entity.ClrType == typeof(AssetAllocation));
        Assert.Equal(expected, borrowed.RowFilter.ToString());
        Assert.All(
            plan.Where(entry => entry.Entity.ClrType != typeof(AssetAllocation)),
            entry => Assert.Equal(PlanningRowFilter.None, entry.RowFilter));
        Assert.DoesNotContain(
            ModuleDataScope.EntitiesFor(db.Model, module),
            entity => entity.ClrType == typeof(AssetAllocation));
    }

    [Theory]
    [InlineData(PlatformModules.ResourcePlanning, true)]
    [InlineData(PlatformModules.Pmoc, false)]
    public void Planning_owns_every_allocation_and_pmoc_owns_none(string module, bool ownsAllocations)
    {
        using var db = ModelOnlyDb();

        var plan = ModuleDataScope.PlanFor(db.Model, module);

        Assert.Equal(ownsAllocations, plan.Any(entry => entry.Entity.ClrType == typeof(AssetAllocation)));
        Assert.All(plan, entry => Assert.Equal(PlanningRowFilter.None, entry.RowFilter));
    }

    [Fact]
    public void Catalog_allocations_are_deleted_before_the_planning_rows_they_point_at()
    {
        using var db = ModelOnlyDb();

        var order = ModuleDataScope.PlanFor(db.Model, PlatformModules.Catalog)
            .Select(entry => entry.Entity.ClrType)
            .ToList();

        Assert.True(
            order.IndexOf(typeof(CatalogOrderItemResourceAllocation)) < order.IndexOf(typeof(AssetAllocation)));
        Assert.True(order.IndexOf(typeof(AssetAllocation)) < order.IndexOf(typeof(CatalogOrderItem)));
    }

    [Theory]
    [InlineData("rentals", "rentals", null)]
    [InlineData("Catalog", "catalog", null)]
    [InlineData("os", "os", null)]
    [InlineData("PMOC", "pmoc", null)]
    [InlineData("resource-planning", "resource-planning", null)]
    [InlineData("aluguel", "rentals", null)]
    [InlineData("inventory", "inventory", ModuleDataScope.NotPurgeableCode)]
    [InlineData("maintenance", "maintenance", ModuleDataScope.NotPurgeableCode)]
    [InlineData("core", "core", ModuleDataScope.NotPurgeableCode)]
    [InlineData("assets", "assets", ModuleDataScope.NotPurgeableCode)]
    [InlineData("billing", "billing", ModuleDataScope.NotPurgeableCode)]
    [InlineData("CORE", "core", ModuleDataScope.NotPurgeableCode)]
    [InlineData("bogus", null, ModuleDataScope.UnknownModuleCode)]
    [InlineData("", null, ModuleDataScope.UnknownModuleCode)]
    [InlineData(null, null, ModuleDataScope.UnknownModuleCode)]
    public void Resolve_maps_keys_to_a_module_or_an_error_code(string? key, string? module, string? code)
    {
        var resolved = ModuleDataScope.Resolve(key);

        Assert.Equal(module, resolved.Module);
        Assert.Equal(code, resolved.ErrorCode);
    }

    [Fact]
    public void The_shared_order_ignores_foreign_keys_that_leave_the_candidate_set()
    {
        var builder = new ModelBuilder();
        builder.Entity<Parent>().ToTable("parents", "scope").HasKey(row => row.Id);
        builder.Entity<Parent>().Property(row => row.TenantId);
        builder.Entity<Child>().ToTable("children", "scope").HasKey(row => row.Id);
        builder.Entity<Child>().HasOne<Parent>().WithMany().HasForeignKey(row => row.ParentId);
        builder.Entity<Outsider>().ToTable("outsiders", "other").HasKey(row => row.Id);
        builder.Entity<Outsider>().HasOne<Parent>().WithMany().HasForeignKey(row => row.ParentId);
        var model = builder.FinalizeModel();
        var pool = model.GetEntityTypes()
            .Where(entity => entity.GetSchema() == "scope")
            .ToArray();

        var covered = TenantDeletionOrder.Cover(pool, entity => entity.FindProperty("TenantId") is not null);
        var order = TenantDeletionOrder.Order(covered);

        Assert.Equal([typeof(Child), typeof(Parent)], order.Select(entity => entity.ClrType));
        Assert.DoesNotContain(covered, entity => entity.ClrType == typeof(Outsider));
    }

    private static void AssertLeafFirst(IReadOnlyList<IEntityType> order)
    {
        Assert.Equal(order.Count, order.Distinct().Count());
        foreach (var entity in order)
        {
            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var principal = foreignKey.PrincipalEntityType;
                if (principal == entity || !order.Contains(principal))
                    continue;

                Assert.True(
                    order.ToList().IndexOf(entity) < order.ToList().IndexOf(principal),
                    $"{entity.Name} must be purged before {principal.Name}.");
            }
        }
    }

    private sealed class Future
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    private sealed class Parent
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    private sealed class Child
    {
        public Guid Id { get; set; }
        public Guid ParentId { get; set; }
    }

    private sealed class Outsider
    {
        public Guid Id { get; set; }
        public Guid ParentId { get; set; }
    }
}
