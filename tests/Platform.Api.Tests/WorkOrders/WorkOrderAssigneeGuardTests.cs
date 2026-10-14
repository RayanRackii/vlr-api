using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Tests.Authorization;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderAssigneeGuardTests
{
    [Fact]
    public async Task Assignee_from_another_tenant_is_not_found_when_ambient_filter_is_off()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        var otherTenant = new Tenant("Other OS", "88888888000191");
        harness.Db.Tenants.Add(otherTenant);
        harness.Db.TenantModules.Add(new TenantModule(otherTenant.Id, PlatformModules.WorkOrders, isActive: true));
        var role = new Role(otherTenant.Id, "Executor", isSystemRole: false);
        harness.Db.Roles.Add(role);
        var permission = harness.Db.Permissions.Local.First(item => item.Key == Permissions.Os.WorkOrdersExecute);
        harness.Db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        var foreign = new User(otherTenant.Id, "foreign-exec", "Foreign", "foreign@test.com");
        harness.Db.Users.Add(foreign);
        harness.Db.UserRoles.Add(new UserRole(foreign.Id, role.Id));
        await harness.Db.SaveChangesAsync();

        harness.TenantProvider.TenantId = null;

        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            WorkOrderAssigneeGuard.ResolveOptionalAsync(
                harness.Db,
                harness.Resolver,
                harness.Tenant.Id,
                foreign.Id,
                CancellationToken.None));

        Assert.Contains("was not found", missing.Message, StringComparison.Ordinal);
        Assert.Contains(foreign.Id.ToString(), missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assignee_in_the_explicit_tenant_with_execute_is_accepted()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.WorkOrders, isActive: true));
        var role = harness.AddCustomRole("Executor", Permissions.Os.WorkOrdersExecute);
        var executor = harness.AddUser("local-exec");
        harness.Assign(executor, role);
        await harness.Db.SaveChangesAsync();

        var resolved = await WorkOrderAssigneeGuard.ResolveOptionalAsync(
            harness.Db,
            harness.Resolver,
            harness.Tenant.Id,
            executor.Id,
            CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(executor.Id, resolved!.Id);
        Assert.Equal(harness.Tenant.Id, resolved.TenantId);
    }
}
