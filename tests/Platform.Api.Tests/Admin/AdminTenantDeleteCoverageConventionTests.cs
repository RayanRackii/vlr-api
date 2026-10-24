using Microsoft.EntityFrameworkCore;
using Platform.Api.Services.Tenancy;
using Platform.Api.Tests.Fakes;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Admin;

public sealed class AdminTenantDeleteCoverageConventionTests
{
    [Fact]
    public void Every_model_entity_with_TenantId_is_in_the_executable_purge_plan()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=model_only;Username=postgres;Password=postgres")
                .Options,
            new FakeTenantProvider());

        var plan = TenantPurger.BuildDeletionOrder(db.Model);
        var expected = db.Model.GetEntityTypes().Where(entity => entity.FindProperty("TenantId") is not null);
        Assert.All(expected, entity => Assert.Contains(entity, plan));
        Assert.Contains(plan, entity => entity.ClrType == typeof(UserRole));
        Assert.Contains(plan, entity => entity.ClrType == typeof(RolePermission));
        Assert.Contains(plan, entity => entity.ClrType == typeof(UserInviteRole));
        Assert.Contains(plan, entity => entity.ClrType == typeof(NotificationDeliveryAttempt));
        Assert.DoesNotContain(plan, entity => entity.ClrType == typeof(Permission));
        Assert.DoesNotContain(plan, entity => entity.ClrType == typeof(NotificationTemplate));
        foreach (var entity in plan)
        {
            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var principal = foreignKey.PrincipalEntityType;
                if (principal != entity && plan.Contains(principal))
                    Assert.True(plan.ToList().IndexOf(entity) < plan.ToList().IndexOf(principal),
                        $"{entity.Name} must be purged before {principal.Name}.");
            }
        }
    }

    [Fact]
    public void New_tenant_entity_and_its_unscoped_child_are_covered_without_a_hand_list()
    {
        var model = new ModelBuilder();
        model.Entity<NewTenantEntity>().ToTable("new_tenant_entities").HasKey(row => row.Id);
        model.Entity<NewTenantEntity>().Property(row => row.TenantId);
        model.Entity<NewChild>().ToTable("new_children").HasKey(row => row.Id);
        model.Entity<NewChild>().HasOne<NewTenantEntity>().WithMany().HasForeignKey(row => row.ParentId);

        var plan = TenantPurger.BuildDeletionOrder(model.FinalizeModel());

        Assert.Equal([typeof(NewChild), typeof(NewTenantEntity)], plan.Select(entity => entity.ClrType));
    }

    [Fact]
    public void Unsupported_unscoped_self_reference_is_rejected_before_executing_a_plan()
    {
        var model = new ModelBuilder();
        model.Entity<NewTenantEntity>().ToTable("new_tenant_entities").HasKey(row => row.Id);
        model.Entity<NewTenantEntity>().Property(row => row.TenantId);
        model.Entity<NewChild>().ToTable("new_children").HasKey(row => row.Id);
        model.Entity<NewChild>().HasOne<NewTenantEntity>().WithMany().HasForeignKey(row => row.ParentId);
        model.Entity<NewChild>().HasOne<NewChild>().WithMany().HasForeignKey("PreviousChildId");

        var error = Assert.Throws<InvalidOperationException>(() =>
            TenantPurger.BuildDeletionOrder(model.FinalizeModel()));

        Assert.Contains("Unsupported unscoped self-reference", error.Message);
    }

    private sealed class NewTenantEntity
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
    }

    private sealed class NewChild
    {
        public Guid Id { get; set; }
        public Guid ParentId { get; set; }
    }
}
