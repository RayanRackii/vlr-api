using Platform.Api.Services.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Controllers;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

/// <summary>
/// Relational coverage for tenant identity conflicts. In-memory tests never raise
/// <see cref="PostgresException"/>, so they cannot tell an identity unique index
/// from any other unique index.
/// </summary>
public sealed class AdminTenantIdentityUniqueViolationTests : IClassFixture<PostgresContainerFixture>
{
    private const string ConflictMessage =
        "A tenant with the same TaxId or Subdomain already exists.";

    private const int OverlapAttempts = 3;

    private readonly PostgresContainerFixture _postgres;

    public AdminTenantIdentityUniqueViolationTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Update_keeps_own_tax_id_and_subdomain()
    {
        var factory = RequireFactory();
        var pair = await CreatePairAsync(factory);

        var action = await UpdateAsync(
            factory,
            new FakeTenantProvider { TenantId = pair.Other.Id },
            pair.Target.Id,
            UpdateRequest(pair.Target));

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var updated = Assert.IsType<TenantAdminResponseDto>(ok.Value);
        Assert.Equal(pair.Target.TaxId, updated.TaxId);
        Assert.Equal(pair.Target.Subdomain, updated.Subdomain);
        Assert.Equal(pair.Target.TaxId, await TaxIdAsync(factory, pair.Target.Id));
        Assert.Equal(2, await ModuleCountAsync(factory, pair.Target.Id));
        Assert.Equal(1, await ModuleCountAsync(factory, pair.Other.Id));
    }

    [DockerFact]
    public async Task Update_to_another_tenants_tax_id_returns_409()
    {
        var factory = RequireFactory();
        var pair = await CreatePairAsync(factory);

        var action = await UpdateAsync(
            factory,
            new FakeTenantProvider { TenantId = pair.Other.Id },
            pair.Target.Id,
            UpdateRequest(pair.Target, taxId: pair.Other.TaxId));

        AssertConflict(action);
        Assert.Equal(pair.Target.TaxId, await TaxIdAsync(factory, pair.Target.Id));
    }

    [DockerFact]
    public async Task Update_to_another_tenants_subdomain_returns_409()
    {
        var factory = RequireFactory();
        var pair = await CreatePairAsync(factory);

        var action = await UpdateAsync(
            factory,
            new FakeTenantProvider { TenantId = pair.Other.Id },
            pair.Target.Id,
            UpdateRequest(pair.Target, subdomain: pair.Other.Subdomain));

        AssertConflict(action);
        Assert.Equal(pair.Target.Subdomain, await SubdomainAsync(factory, pair.Target.Id));
    }

    [DockerFact]
    public async Task Concurrent_update_to_the_same_new_tax_id_is_the_tax_id_unique_violation()
    {
        await AssertConcurrentIdentityConflictAsync(
            NewTaxId,
            (tenant, value) => UpdateRequest(tenant, taxId: value),
            "ix_tenants_tax_id");
    }

    [DockerFact]
    public async Task Concurrent_update_to_the_same_new_subdomain_is_the_subdomain_unique_violation()
    {
        await AssertConcurrentIdentityConflictAsync(
            () => $"shared-{Guid.NewGuid():N}"[..24],
            (tenant, value) => UpdateRequest(tenant, subdomain: value),
            "ix_tenants_subdomain");
    }

    [DockerFact]
    public async Task Concurrent_module_enable_succeeds_and_creates_one_default_menu_item_per_module()
    {
        var factory = RequireFactory();
        await InstallUpdateDelayAsync(factory);
        try
        {
            for (var attempt = 1; attempt <= OverlapAttempts; attempt++)
            {
                var created = await CreateTenantAsync(factory, [PlatformModules.Catalog]);
                var request = UpdateRequest(
                    created,
                    modules: [PlatformModules.Catalog, PlatformModules.Rentals]);
                var outcomes = await Task.WhenAll(
                    CaptureUpdateAsync(factory, created.Id, request),
                    CaptureUpdateAsync(factory, created.Id, request));

                Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded, outcome.Error?.ToString()));
                Assert.Equal(created.TaxId, await TaxIdAsync(factory, created.Id));
                Assert.Equal(created.Subdomain, await SubdomainAsync(factory, created.Id));
                Assert.Equal(2, await ModuleCountAsync(factory, created.Id));
                Assert.Equal(2, await MenuItemCountAsync(factory, created.Id));
                return;
            }

            Assert.Fail("Concurrent module enable requests did not overlap.");
        }
        finally
        {
            await RemoveUpdateDelayAsync(factory);
        }
    }

    private async Task AssertConcurrentIdentityConflictAsync(
        Func<string> takenFactory,
        Func<TenantAdminResponseDto, string, UpdateTenantRequestDto> requestFor,
        string constraintName)
    {
        var factory = RequireFactory();
        await InstallUpdateDelayAsync(factory);
        try
        {
            for (var attempt = 1; attempt <= OverlapAttempts; attempt++)
            {
                var taken = takenFactory();
                var pair = await CreatePairAsync(factory);
                var outcomes = await Task.WhenAll(
                    CaptureUpdateAsync(factory, pair.Target.Id, requestFor(pair.Target, taken)),
                    CaptureUpdateAsync(factory, pair.Other.Id, requestFor(pair.Other, taken)));

                if (outcomes.All(outcome => outcome.Succeeded))
                {
                    continue;
                }

                Assert.Equal(1, outcomes.Count(outcome => outcome.Succeeded));
                var failure = Assert.Single(outcomes, outcome => !outcome.Succeeded).Error;
                var conflict = Assert.IsType<InvalidOperationException>(failure);
                Assert.Equal(ConflictMessage, conflict.Message);
                var postgres = FindPostgresException(conflict);
                if (postgres is null)
                {
                    continue;
                }

                Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
                Assert.Equal(constraintName, postgres.ConstraintName);

                var taxIds = new[]
                {
                    await TaxIdAsync(factory, pair.Target.Id),
                    await TaxIdAsync(factory, pair.Other.Id),
                };
                var subdomains = new[]
                {
                    await SubdomainAsync(factory, pair.Target.Id),
                    await SubdomainAsync(factory, pair.Other.Id),
                };
                if (constraintName == "ix_tenants_tax_id")
                {
                    Assert.Single(taxIds, taxId => taxId == taken);
                }
                else
                {
                    Assert.Single(subdomains, subdomain => subdomain == taken);
                }

                return;
            }

            Assert.Fail($"Concurrent updates did not overlap on {constraintName}.");
        }
        finally
        {
            await RemoveUpdateDelayAsync(factory);
        }
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static async Task<ActionResult<TenantAdminResponseDto>> UpdateAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        Guid tenantId,
        UpdateTenantRequestDto request)
    {
        await using var db = factory.Create(tenantProvider);
        var controller = new AdminTenantsController(CreateService(db));
        return await controller.Update(tenantId, request, CancellationToken.None);
    }

    private static async Task<UpdateOutcome> CaptureUpdateAsync(
        PostgresAppDbFactory factory,
        Guid tenantId,
        UpdateTenantRequestDto request)
    {
        var tenantProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var db = factory.Create(tenantProvider);
        try
        {
            await CreateService(db).UpdateAsync(tenantId, request, CancellationToken.None);
            return new UpdateOutcome(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateOutcome(false, ex);
        }
    }

    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private static async Task<TenantPair> CreatePairAsync(PostgresAppDbFactory factory)
    {
        var target = await CreateTenantAsync(factory, [PlatformModules.Rentals, PlatformModules.Catalog]);
        var other = await CreateTenantAsync(factory, [PlatformModules.Inventory]);
        return new TenantPair(target, other);
    }

    private static async Task<TenantAdminResponseDto> CreateTenantAsync(
        PostgresAppDbFactory factory,
        IReadOnlyList<string> modules)
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = factory.Create(tenantProvider);
        return await CreateService(db).CreateAsync(CreateRequest(modules), CancellationToken.None);
    }

    private static async Task InstallUpdateDelayAsync(PostgresAppDbFactory factory)
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = factory.Create(tenantProvider);
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION core.rolvix_test_delay_tenant_update()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $fn$
            BEGIN
                PERFORM pg_sleep(1);
                RETURN NEW;
            END;
            $fn$;
            """);
        await db.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER IF EXISTS rolvix_test_delay_tenant_update ON core.tenants");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER rolvix_test_delay_tenant_update
            BEFORE UPDATE ON core.tenants
            FOR EACH ROW
            EXECUTE FUNCTION core.rolvix_test_delay_tenant_update()
            """);
    }

    private static async Task RemoveUpdateDelayAsync(PostgresAppDbFactory factory)
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = factory.Create(tenantProvider);
        await db.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER IF EXISTS rolvix_test_delay_tenant_update ON core.tenants");
        await db.Database.ExecuteSqlRawAsync(
            "DROP FUNCTION IF EXISTS core.rolvix_test_delay_tenant_update()");
    }

    private static async Task<string> TaxIdAsync(PostgresAppDbFactory factory, Guid tenantId)
    {
        await using var db = factory.Create(new FakeTenantProvider { TenantId = null });
        return await db.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.TaxId)
            .SingleAsync();
    }

    private static async Task<string?> SubdomainAsync(PostgresAppDbFactory factory, Guid tenantId)
    {
        await using var db = factory.Create(new FakeTenantProvider { TenantId = null });
        return await db.Tenants
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.Subdomain)
            .SingleAsync();
    }

    private static async Task<int> ModuleCountAsync(PostgresAppDbFactory factory, Guid tenantId)
    {
        await using var db = factory.Create(new FakeTenantProvider { TenantId = null });
        return await db.TenantModules
            .IgnoreQueryFilters()
            .CountAsync(module => module.TenantId == tenantId);
    }

    private static async Task<int> MenuItemCountAsync(PostgresAppDbFactory factory, Guid tenantId)
    {
        await using var db = factory.Create(new FakeTenantProvider { TenantId = null });
        return await db.TenantModuleMenuItems
            .IgnoreQueryFilters()
            .CountAsync(item => item.TenantId == tenantId);
    }

    private static AdminTenantService CreateService(AppDbContext db) =>
        new(
            db,
            new FakeTenantUserAdminService(),
            new FakePlatformAdminMembershipService(),
            new FakeTenantAccessBootstrapper(),
            NullLogger<AdminTenantService>.Instance,
            new TenantProvisioner(db, NullLogger<TenantProvisioner>.Instance),
            new TenantPurger(db, new FakeSupabaseAuthAdminClient(), Options.Create(new PlatformAdminOptions()), NullLogger<TenantPurger>.Instance, Options.Create(new StorageOptions()), new RecordingStorageProvider()));

    private static CreateTenantRequestDto CreateRequest(IReadOnlyList<string> modules)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new CreateTenantRequestDto
        {
            LegalName = "Clube Identidade",
            TaxId = NewTaxId(),
            Subdomain = $"id-{suffix}"[..20],
            ActiveModules = modules,
            AssetFamilyKeys = [AssetFamilyKeys.Generic],
        };
    }

    private static UpdateTenantRequestDto UpdateRequest(
        TenantAdminResponseDto tenant,
        string? taxId = null,
        string? subdomain = null,
        IReadOnlyList<string>? modules = null) =>
        new()
        {
            LegalName = tenant.LegalName,
            TaxId = taxId ?? tenant.TaxId,
            Subdomain = subdomain ?? tenant.Subdomain ?? throw new InvalidOperationException("Subdomain is required."),
            ActiveModules = modules ?? tenant.ActiveModules.Select(module => module.ModuleName).ToArray(),
            AssetFamilyKeys = [AssetFamilyKeys.Generic],
        };

    private static string NewTaxId() => Guid.NewGuid().ToString("N")[..14];

    private static void AssertConflict(ActionResult<TenantAdminResponseDto> action)
    {
        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal(
            ConflictMessage,
            conflict.Value?.GetType().GetProperty("error")?.GetValue(conflict.Value));
    }

    private readonly record struct UpdateOutcome(bool Succeeded, Exception? Error);

    private readonly record struct TenantPair(TenantAdminResponseDto Target, TenantAdminResponseDto Other);
}
