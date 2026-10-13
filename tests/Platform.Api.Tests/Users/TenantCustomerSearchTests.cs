using Microsoft.EntityFrameworkCore;
using Platform.Api.Common;
using Platform.Api.Modules.Customers.Services;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Users;

public sealed class TenantCustomerSearchTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public TenantCustomerSearchTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Search_escapes_like_wildcards_and_stays_inside_the_tenant()
    {
        var factory = _postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");
        var tenantA = new Tenant("Search A", "33333333000191", subdomain: "searcha");
        var tenantB = new Tenant("Search B", "44444444000191", subdomain: "searchb");
        var literal = new Customer
        {
            TenantId = tenantA.Id,
            Name = "10%_off",
            Email = "literal@search.test",
        };
        var wildcardLookalike = new Customer
        {
            TenantId = tenantA.Id,
            Name = "100",
            Email = "hundred@search.test",
        };
        var otherTenant = new Customer
        {
            TenantId = tenantB.Id,
            Name = "10%_off",
            Email = "other@search.test",
        };

        var seedProvider = new FakeTenantProvider();
        await using (var db = factory.Create(seedProvider))
        {
            db.Tenants.AddRange(tenantA, tenantB);
            db.Customers.AddRange(literal, wildcardLookalike, otherTenant);
            await db.SaveChangesAsync();
        }

        var scoped = new FakeTenantProvider { TenantId = tenantA.Id };
        await using var read = factory.Create(scoped);
        var directory = new TenantCustomerDirectory(read, scoped);
        Assert.True(DirectoryPage.TryCreate("10%_", 1, 20, out var page, out _));

        var result = await directory.ListAsync(page, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(literal.Id, item.Id);
        Assert.Equal(1, result.Total);
        Assert.DoesNotContain(result.Items, row => row.Id == otherTenant.Id);
        Assert.DoesNotContain(result.Items, row => row.Id == wildcardLookalike.Id);
    }

    [DockerFact]
    public async Task Staff_search_escapes_underscore_and_skips_platform_admin_email()
    {
        var factory = _postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");
        var tenant = new Tenant("Search Staff", "55555555000191", subdomain: "searchstaff");
        var visible = new User(tenant.Id, "visible-sub", "a_b Person", "visible@search.test");
        var underscoreWildcard = new User(tenant.Id, "wild-sub", "axb Person", "wild@search.test");
        var platform = new User(tenant.Id, "platform-sub", "a_b Platform", "platform@search.test");

        var seedProvider = new FakeTenantProvider();
        await using (var db = factory.Create(seedProvider))
        {
            db.Tenants.Add(tenant);
            db.Users.AddRange(visible, underscoreWildcard, platform);
            await db.SaveChangesAsync();
        }

        var scoped = new FakeTenantProvider { TenantId = tenant.Id };
        await using var read = factory.Create(scoped);
        var users = new UserDirectoryService(
            read,
            scoped,
            new FakePlatformAdminChecker("platform@search.test"),
            TestPermissionResolvers.Create(read, scoped),
            new Platform.Api.Authorization.RbacGrantGuard(
                read,
                TestPermissionResolvers.Create(read, scoped),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Platform.Api.Authorization.RbacGrantGuard>.Instance),
            new FakeTrialGuard(),
            new NotificationQueue(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new FakeHostEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UserDirectoryService>.Instance);
        Assert.True(DirectoryPage.TryCreate("a_b", 1, 20, out var page, out _));

        var result = await users.ListAsync(page, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(visible.Id, item.Id);
    }
}
