using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Platform.Api.Jobs;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Proves raw <c>FOR UPDATE</c> helpers cannot lock a row owned by another tenant.
/// A holder keeps the victim row locked. The same id with the foreign tenant id must
/// return without waiting, and <c>pgrowlocks</c> must not show that backend on the row. The owning
/// tenant id must still wait on the holder (<c>55P03</c>).
/// </summary>
public sealed class TenantScopedForUpdateLockTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public TenantScopedForUpdateLockTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Reservation_lock_rejects_an_empty_tenant_id()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            ReservationLocks.LockByReservationIdAsync(
                db,
                Guid.Empty,
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Contains("Tenant id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_and_asset_locks_reject_an_empty_tenant_id()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());

        var plan = await Assert.ThrowsAsync<ArgumentException>(() =>
            PmocPlanAssetLock.LockPlanRowAsync(
                db,
                Guid.Empty,
                Guid.NewGuid(),
                CancellationToken.None));
        var asset = await Assert.ThrowsAsync<ArgumentException>(() =>
            PmocPlanAssetLock.LockAssetRowAsync(
                db,
                Guid.Empty,
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.Contains("Tenant id", plan.Message, StringComparison.Ordinal);
        Assert.Contains("Tenant id", asset.Message, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task Reservation_lock_does_not_touch_another_tenants_row()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);

        await AssertLockStaysInsideTenantAsync(
            factory,
            "rentals",
            "reservations",
            seed.OwnerTenantId,
            seed.ForeignTenantId,
            seed.OwnerReservationId,
            seed.ForeignReservationId,
            (db, tenantId, rowId, cancellationToken) =>
                ReservationLocks.LockByReservationIdAsync(db, tenantId, rowId, cancellationToken));
    }

    [DockerFact]
    public async Task Maintenance_plan_lock_does_not_touch_another_tenants_row()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);

        await AssertLockStaysInsideTenantAsync(
            factory,
            "pmoc",
            "maintenance_plans",
            seed.OwnerTenantId,
            seed.ForeignTenantId,
            seed.OwnerPlanId,
            seed.ForeignPlanId,
            (db, tenantId, rowId, cancellationToken) =>
                PmocPlanAssetLock.LockPlanRowAsync(db, tenantId, rowId, cancellationToken));
    }

    [DockerFact]
    public async Task Asset_lock_does_not_touch_another_tenants_row()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);

        await AssertLockStaysInsideTenantAsync(
            factory,
            "assets",
            "assets",
            seed.OwnerTenantId,
            seed.ForeignTenantId,
            seed.OwnerAssetId,
            seed.ForeignAssetId,
            (db, tenantId, rowId, cancellationToken) =>
                PmocPlanAssetLock.LockAssetRowAsync(db, tenantId, rowId, cancellationToken));
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static async Task AssertLockStaysInsideTenantAsync(
        PostgresAppDbFactory factory,
        string schema,
        string table,
        Guid ownerTenantId,
        Guid foreignTenantId,
        Guid ownerRowId,
        Guid foreignRowId,
        Func<AppDbContext, Guid, Guid, CancellationToken, Task> lockRow)
    {
        await using (var setup = factory.Create(new FakeTenantProvider()))
        {
            await setup.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS pgrowlocks");
        }

        await using var holder = factory.Create(new FakeTenantProvider());
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            HoldRowSql(schema, table, ownerRowId, ownerTenantId));

        var holderPid = await BackendPidAsync(holder);
        var heldRows = await LockedRowsAsync(factory, schema, table);
        var heldVictim = Assert.Single(heldRows, row => row.Id == ownerRowId);
        Assert.Equal(ownerTenantId, heldVictim.TenantId);
        Assert.Contains(holderPid, heldVictim.Pids);
        Assert.Contains("For Update", heldVictim.Modes);

        await using var foreign = factory.Create(new FakeTenantProvider());
        await using var foreignTx = await foreign.Database.BeginTransactionAsync();
        await foreign.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '750ms'");
        using var foreignTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var foreignOnOwner = await Record.ExceptionAsync(() =>
            lockRow(foreign, foreignTenantId, ownerRowId, foreignTimeout.Token));
        Assert.Null(foreignOnOwner);

        var foreignPid = await BackendPidAsync(foreign);
        var afterForeignOwnerId = await LockedRowsAsync(factory, schema, table);
        var victimAfterForeign = Assert.Single(afterForeignOwnerId, row => row.Id == ownerRowId);
        Assert.Contains(holderPid, victimAfterForeign.Pids);
        Assert.DoesNotContain(foreignPid, victimAfterForeign.Pids);
        Assert.DoesNotContain(
            afterForeignOwnerId,
            row => row.TenantId != foreignTenantId && row.Pids.Contains(foreignPid));

        var foreignOnOwn = await Record.ExceptionAsync(() =>
            lockRow(foreign, foreignTenantId, foreignRowId, foreignTimeout.Token));
        Assert.Null(foreignOnOwn);

        var foreignLocks = await LockedRowsAsync(factory, schema, table);
        var lockedOwn = Assert.Single(foreignLocks, row => row.Id == foreignRowId);
        Assert.Equal(foreignTenantId, lockedOwn.TenantId);
        Assert.Contains(foreignPid, lockedOwn.Pids);
        Assert.Contains("For Update", lockedOwn.Modes);
        var victimWhileForeignHoldsOwn = Assert.Single(foreignLocks, row => row.Id == ownerRowId);
        Assert.DoesNotContain(foreignPid, victimWhileForeignHoldsOwn.Pids);
        Assert.DoesNotContain(
            foreignLocks,
            row => row.TenantId != foreignTenantId && row.Pids.Contains(foreignPid));

        await using var owner = factory.Create(new FakeTenantProvider());
        await using var ownerTx = await owner.Database.BeginTransactionAsync();
        await owner.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '750ms'");
        using var ownerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ownerWait = await Record.ExceptionAsync(() =>
            lockRow(owner, ownerTenantId, ownerRowId, ownerTimeout.Token));
        var postgres = FindPostgres(ownerWait);
        Assert.NotNull(postgres);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, postgres.SqlState);
    }

    private static async Task<int> BackendPidAsync(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_backend_pid()";
        if (db.Database.CurrentTransaction is not null)
        {
            command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        }

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<List<LockedRow>> LockedRowsAsync(
        PostgresAppDbFactory factory,
        string schema,
        string table)
    {
        if (!IsLockTarget(schema, table))
        {
            throw new ArgumentException("Unsupported lock target.");
        }

        await using var observer = factory.Create(new FakeTenantProvider());
        var connection = observer.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT row_data.id, row_data.tenant_id, locks.pids, locks.modes
            FROM pgrowlocks(@qualified) AS locks
            JOIN {schema}.{table} AS row_data
              ON row_data.ctid = locks.locked_row
            """;
        AddParameter(command, "qualified", $"{schema}.{table}");

        var rows = new List<LockedRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LockedRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetFieldValue<int[]>(2),
                reader.GetFieldValue<string[]>(3)));
        }

        return rows;
    }

    private static FormattableString HoldRowSql(string schema, string table, Guid rowId, Guid tenantId) =>
        (schema, table) switch
        {
            ("rentals", "reservations") =>
                $"SELECT 1 FROM rentals.reservations WHERE id = {rowId} AND tenant_id = {tenantId} FOR UPDATE",
            ("pmoc", "maintenance_plans") =>
                $"SELECT 1 FROM pmoc.maintenance_plans WHERE id = {rowId} AND tenant_id = {tenantId} FOR UPDATE",
            ("assets", "assets") =>
                $"SELECT 1 FROM assets.assets WHERE id = {rowId} AND tenant_id = {tenantId} FOR UPDATE",
            _ => throw new ArgumentException("Unsupported lock target."),
        };

    private static bool IsLockTarget(string schema, string table) =>
        (schema, table) is ("rentals", "reservations")
            or ("pmoc", "maintenance_plans")
            or ("assets", "assets");

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static PostgresException? FindPostgres(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private static async Task<Seed> SeedAsync(PostgresAppDbFactory factory)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var owner = CreateTenant("Owner Club");
        var foreign = CreateTenant("Foreign Club");
        var ownerGraph = CreateGraph(owner);
        var foreignGraph = CreateGraph(foreign);

        db.Tenants.AddRange(owner, foreign);
        db.Units.AddRange(ownerGraph.Unit, foreignGraph.Unit);
        db.Customers.AddRange(ownerGraph.Customer, foreignGraph.Customer);
        db.AssetCategories.AddRange(ownerGraph.Category, foreignGraph.Category);
        db.AssetFamilies.AddRange(ownerGraph.Family, foreignGraph.Family);
        db.Assets.AddRange(ownerGraph.Asset, foreignGraph.Asset);
        db.MaintenancePlans.AddRange(ownerGraph.Plan, foreignGraph.Plan);
        db.Reservations.AddRange(ownerGraph.Reservation, foreignGraph.Reservation);
        await db.SaveChangesAsync();

        return new Seed(
            owner.Id,
            foreign.Id,
            ownerGraph.Reservation.Id,
            foreignGraph.Reservation.Id,
            ownerGraph.Plan.Id,
            foreignGraph.Plan.Id,
            ownerGraph.Asset.Id,
            foreignGraph.Asset.Id);
    }

    private static Tenant CreateTenant(string name)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        return new Tenant(name, UniqueTaxId(), subdomain: $"lock-{suffix}");
    }

    private static string UniqueTaxId() =>
        Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(CultureInfo.InvariantCulture);

    private static TenantGraph CreateGraph(Tenant tenant)
    {
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "AC" };
        var family = new AssetFamily
        {
            Key = $"fam-{Guid.NewGuid():N}"[..32],
            Label = "Family",
            FieldSchemaJson = "{}",
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Split",
            Tag = $"AC-{Guid.NewGuid():N}"[..12],
            Status = AssetStatus.Active,
        };
        var plan = new MaintenancePlan
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            Name = "Plano",
            IntervalDays = 30,
            FirstDueDate = new DateOnly(2026, 10, 1),
            AssetCategoryId = category.Id,
            IsActive = true,
            AutoGenerateEnabled = false,
        };
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Cliente",
            Email = $"cliente-{Guid.NewGuid():N}@lock.test",
        };
        var reservation = new Reservation
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CustomerId = customer.Id,
            CustomerName = customer.Name,
            CustomerWhatsApp = "11999999999",
            StartDateTime = new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.Zero),
            EndDateTime = new DateTimeOffset(2026, 10, 20, 14, 0, 0, TimeSpan.Zero),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 100m,
            DepositPaid = 0m,
        };

        return new TenantGraph(unit, category, family, asset, plan, customer, reservation);
    }

    private sealed record Seed(
        Guid OwnerTenantId,
        Guid ForeignTenantId,
        Guid OwnerReservationId,
        Guid ForeignReservationId,
        Guid OwnerPlanId,
        Guid ForeignPlanId,
        Guid OwnerAssetId,
        Guid ForeignAssetId);

    private sealed record TenantGraph(
        Unit Unit,
        AssetCategory Category,
        AssetFamily Family,
        Asset Asset,
        MaintenancePlan Plan,
        Customer Customer,
        Reservation Reservation);

    private readonly record struct LockedRow(Guid Id, Guid TenantId, int[] Pids, string[] Modes);
}
