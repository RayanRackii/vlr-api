using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.Billing;

/// <summary>
/// The B1 migration against a real PostgreSQL: the nine-row seed, the backfill of existing tenants
/// (one current subscription each; trial vs manual rows; package by active modules), idempotence of
/// the data steps and the permission grants. Own fixture: it rolls the schema back to the step before
/// the migration (its Down only drops the billing tables and the lead columns).
/// </summary>
public sealed class BillingMigrationPostgresTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    private const string Before = "20261129100000_SeedTenantSettingsForTeacherLessonsAndBookingUi";
    private const string Target = "20261130100000_AddBillingPlansAndSubscriptions";

    // The EF model reads every tenant_subscriptions column, so the additive B3 columns must exist before it queries.
    private const string ModelReady = "20261202100000_AddSubscriptionProviderBillingState";

    [DockerFact]
    public async Task Migration_seeds_the_catalog_backfills_each_tenant_once_and_is_idempotent()
    {
        var factory = Assert.IsType<PostgresAppDbFactory>(postgres.Factory);
        var provider = new FakeTenantProvider();
        await using var db = factory.Create(provider);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);

        var now = DateTimeOffset.UtcNow;
        var trialEnds = now.AddDays(10);
        var trialPurge = now.AddDays(70);
        var trialAmbiguous = await InsertTenantAsync(db, "Trial ambiguous", ["rentals", "inventory", "pmoc", "os"], trial: (trialEnds, trialPurge));
        var trialMaintenance = await InsertTenantAsync(db, "Trial maintenance", ["pmoc"], trial: (trialEnds, trialPurge));
        var maintenance = await InsertTenantAsync(db, "Maintenance", ["inventory", "pmoc", "os"]);
        var rental = await InsertTenantAsync(db, "Rental", ["rentals", "inventory", "catalog"]);
        var rentalOnly = await InsertTenantAsync(db, "Rental only", ["rentals"]);
        var rentalCatalog = await InsertTenantAsync(db, "Rental catalog", ["rentals", "catalog"]);
        var noModules = await InsertTenantAsync(db, "No modules", []);
        var inactiveMaintenance = await InsertTenantAsync(db, "Inactive pmoc", ["rentals"], inactiveModules: ["pmoc"]);
        var adminRoleTenant = maintenance;
        var adminRoleId = Guid.NewGuid();
        var userRoleId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO core.roles (id, tenant_id, name, is_system_role, created_at)
            VALUES ({adminRoleId}, {adminRoleTenant}, 'Admin', true, now()),
                   ({userRoleId}, {adminRoleTenant}, 'User', true, now())
            """);
        var tenantCount = 8;

        await migrator.MigrateAsync(Target);
        await migrator.MigrateAsync(ModelReady);

        // Seed: exactly the nine study rows.
        var plans = await db.PlanCatalogs.AsNoTracking().ToListAsync();
        Assert.Equal(BillingTestData.CatalogRows.Length, plans.Count);
        foreach (var row in BillingTestData.CatalogRows)
        {
            var plan = Assert.Single(plans, p => p.PackageKey == row.Package && p.PlanKey == row.Plan);
            Assert.Equal(row.Monthly, plan.MonthlyPriceCents);
            Assert.Equal(row.Annual, plan.AnnualPriceCents);
            Assert.Equal("BRL", plan.Currency);
            Assert.True(plan.IsActive);
            Assert.Equal(row.Modules, plan.GetModules());
            Assert.Equal(
                new PlanLimitValues(row.Assets, row.Rentable, row.Users, row.Units, row.Whatsapp),
                plan.GetLimits());
        }

        // Backfill: one current row per tenant.
        var subscriptions = await db.TenantSubscriptions.AsNoTracking().ToListAsync();
        Assert.Equal(tenantCount, subscriptions.Count);
        Assert.Equal(tenantCount, subscriptions.Select(s => s.TenantId).Distinct().Count());
        Assert.All(subscriptions, s => Assert.Null(s.EndedAt));

        (TenantSubscription Subscription, PlanCatalog Plan) Of(Guid tenantId)
        {
            var subscription = Assert.Single(subscriptions, s => s.TenantId == tenantId);
            return (subscription, plans.Single(p => p.Id == subscription.PlanCatalogId));
        }

        // Trial tenants: trialing, provider billed, Profissional of the matching package, limits enforced.
        foreach (var (tenantId, package) in new[] { (trialAmbiguous, "reservas"), (trialMaintenance, "manutencao") })
        {
            var (subscription, plan) = Of(tenantId);
            Assert.Equal((package, "profissional"), (plan.PackageKey, plan.PlanKey));
            Assert.Equal(SubscriptionStatuses.Trialing, subscription.Status);
            Assert.Equal(BillingModes.Provider, subscription.BillingMode);
            Assert.False(subscription.LimitsUnlimited);
            Assert.Equal(0, subscription.DiscountPercent);
            Assert.Equal(BillingCycles.Monthly, subscription.BillingCycle);
            Assert.Equal(
                trialEnds.ToUnixTimeSeconds(),
                subscription.CurrentPeriodEnd!.Value.ToUnixTimeSeconds());
        }

        // Other tenants: active, manual, unlimited, 100 percent discount, Completo of the matching package.
        foreach (var (tenantId, package) in new[]
                 {
                     (maintenance, "manutencao"),
                     (rental, "locacao"),
                     (rentalOnly, "reservas"),
                     (rentalCatalog, "locacao"),
                     (noModules, "reservas"),
                     (inactiveMaintenance, "reservas"),
                 })
        {
            var (subscription, plan) = Of(tenantId);
            Assert.Equal((package, "completo"), (plan.PackageKey, plan.PlanKey));
            Assert.Equal(SubscriptionStatuses.Active, subscription.Status);
            Assert.Equal(BillingModes.Manual, subscription.BillingMode);
            Assert.True(subscription.LimitsUnlimited);
            Assert.Equal(100, subscription.DiscountPercent);
            Assert.Null(subscription.CurrentPeriodEnd);
            Assert.Equal(16, subscription.RowVersion.Length);
        }

        // The backfill never touched the tenants themselves (existing trial dates stay as they were).
        var trialTenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == trialAmbiguous);
        Assert.True(trialTenant.IsTrial);
        Assert.Equal(trialEnds.ToUnixTimeSeconds(), trialTenant.TrialEndsAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(trialPurge.ToUnixTimeSeconds(), trialTenant.TrialPurgeAt!.Value.ToUnixTimeSeconds());
        Assert.Null(trialTenant.LeadSource);

        // Permission: Admin got settings.billing, User did not.
        var grants = await db.RolePermissions.AsNoTracking()
            .Where(rp => rp.Permission.Key == Permissions.Core.SettingsBilling)
            .Select(rp => rp.RoleId)
            .ToListAsync();
        Assert.Equal(new[] { adminRoleId }, grants);

        // Idempotent: running the data steps again changes nothing.
        await RunRawAsync(db, BillingMigrationSql.SeedPlanCatalog);
        await RunRawAsync(db, BillingMigrationSql.BackfillSubscriptions);
        await RunRawAsync(db, BillingMigrationSql.GrantBillingPermission);
        Assert.Equal(9, await db.PlanCatalogs.CountAsync());
        Assert.Equal(tenantCount, await db.TenantSubscriptions.CountAsync());
        Assert.Equal(1, await db.RolePermissions.CountAsync(rp => rp.Permission.Key == Permissions.Core.SettingsBilling));

        // The partial unique index refuses a second current subscription for a tenant ...
        var plan0 = plans[0];
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = rental, PlanCatalogId = plan0.Id });
        var violation = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(violation.InnerException).SqlState);
        db.ChangeTracker.Clear();

        // ... but allows one once the previous one ended (history).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE billing.tenant_subscriptions SET ended_at = now() WHERE tenant_id = {rental}");
        db.TenantSubscriptions.Add(new TenantSubscription { TenantId = rental, PlanCatalogId = plan0.Id });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.TenantSubscriptions.CountAsync(s => s.TenantId == rental));
    }

    /// <summary>Runs SQL as is. ExecuteSqlRaw would read the JSON braces of the seed as format items.</summary>
    private static async Task RunRawAsync(Platform.Core.Infrastructure.Persistence.AppDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<Guid> InsertTenantAsync(
        Platform.Core.Infrastructure.Persistence.AppDbContext db,
        string name,
        string[] modules,
        (DateTimeOffset EndsAt, DateTimeOffset PurgeAt)? trial = null,
        string[]? inactiveModules = null)
    {
        var id = Guid.NewGuid();
        var taxId = Guid.NewGuid().ToString("N")[..14];
        if (trial is { } window)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO core.tenants (id, legal_name, tax_id, is_trial, trial_ends_at, trial_purge_at, created_at)
                VALUES ({id}, {name}, {taxId}, true, {window.EndsAt}, {window.PurgeAt}, now())
                """);
        }
        else
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO core.tenants (id, legal_name, tax_id, created_at)
                VALUES ({id}, {name}, {taxId}, now())
                """);
        }

        foreach (var module in modules)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO core.tenant_modules (id, tenant_id, module_name, is_active, created_at)
                VALUES ({Guid.NewGuid()}, {id}, {module}, true, now())
                """);
        }

        foreach (var module in inactiveModules ?? [])
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO core.tenant_modules (id, tenant_id, module_name, is_active, created_at)
                VALUES ({Guid.NewGuid()}, {id}, {module}, false, now())
                """);
        }

        return id;
    }
}
