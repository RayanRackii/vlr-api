using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Api.Modules.ResourcePlanning;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Real-PostgreSQL coverage of the planning writes. The in-memory provider ignores
/// <c>SELECT ... FOR UPDATE</c>, so the serialization the allocations and the profile rely on
/// (the asset-row lock) can only be proven here: two parallel creates on one asset, exactly one
/// wins; a held lock blocks both the allocation create and the hours PUT; the agenda reads a
/// constant number of times; the L1 grant migration is idempotent on existing roles.
/// </summary>
public sealed class PlanningAllocationConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    // 14:00Z, thirty days out: never expired, whatever day the suite runs on.
    private static readonly DateTimeOffset Slot = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(30), TimeSpan.Zero).AddHours(14);

    private readonly PostgresContainerFixture _postgres;

    public PlanningAllocationConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [Fact]
    public async Task Asset_row_lock_rejects_an_empty_tenant_id()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider());

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => AssetRowLock.LockAsync(db, Guid.Empty, [Guid.NewGuid()], CancellationToken.None));

        Assert.Contains("Tenant id", exception.Message, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task Two_parallel_creates_on_one_asset_let_exactly_one_win()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);
        var first = Stack(factory, seed.TenantId, Guid.NewGuid());
        var second = Stack(factory, seed.TenantId, Guid.NewGuid());
        await using var db1 = first.Db;
        await using var db2 = second.Db;

        var results = await Task.WhenAll(
            CaptureAsync(() => first.Service.CreateAsync(Request(seed.AssetId, Slot, Slot.AddHours(2), "A"), default)),
            CaptureAsync(() => second.Service.CreateAsync(Request(seed.AssetId, Slot.AddMinutes(30), Slot.AddHours(3), "B"), default)));

        Assert.Single(results, r => r.Error is null);
        var loser = Assert.Single(results, r => r.Error is not null);
        var ex = Assert.IsType<PlanningException>(loser.Error);
        Assert.Equal(PlanningException.ConflictCode, ex.Code);
        Assert.Single(ex.Conflicts!);

        await using var verify = factory.Create(new FakeTenantProvider { TenantId = seed.TenantId });
        Assert.Equal(1, await verify.AssetAllocations.CountAsync(a => a.AssetId == seed.AssetId));
    }

    [DockerFact]
    public async Task Many_parallel_creates_of_the_same_window_let_exactly_one_win()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);
        var stacks = Enumerable.Range(0, 8).Select(_ => Stack(factory, seed.TenantId, Guid.NewGuid())).ToList();
        try
        {
            var results = await Task.WhenAll(stacks.Select((stack, index) => CaptureAsync(
                () => stack.Service.CreateAsync(Request(seed.AssetId, Slot, Slot.AddHours(1), $"Racer {index}"), default))));

            Assert.Single(results, r => r.Error is null);
            Assert.All(
                results.Where(r => r.Error is not null),
                r => Assert.Equal(PlanningException.ConflictCode, Assert.IsType<PlanningException>(r.Error).Code));
        }
        finally
        {
            foreach (var stack in stacks)
            {
                await stack.Db.DisposeAsync();
            }
        }

        await using var verify = factory.Create(new FakeTenantProvider { TenantId = seed.TenantId });
        Assert.Equal(1, await verify.AssetAllocations.CountAsync(a => a.AssetId == seed.AssetId));
    }

    [DockerFact]
    public async Task Parallel_creates_of_back_to_back_windows_all_succeed_without_a_deadlock()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);
        var stacks = Enumerable.Range(0, 6).Select(_ => Stack(factory, seed.TenantId, Guid.NewGuid())).ToList();
        try
        {
            var results = await Task.WhenAll(stacks.Select((stack, index) => CaptureAsync(
                () => stack.Service.CreateAsync(
                    Request(seed.AssetId, Slot.AddHours(index), Slot.AddHours(index + 1), $"Slot {index}"), default))));

            Assert.All(results, r => Assert.Null(r.Error));
        }
        finally
        {
            foreach (var stack in stacks)
            {
                await stack.Db.DisposeAsync();
            }
        }

        await using var verify = factory.Create(new FakeTenantProvider { TenantId = seed.TenantId });
        Assert.Equal(6, await verify.AssetAllocations.CountAsync(a => a.AssetId == seed.AssetId));
    }

    [DockerFact]
    public async Task Parallel_creates_on_different_assets_do_not_block_each_other()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);
        var first = Stack(factory, seed.TenantId, Guid.NewGuid());
        var second = Stack(factory, seed.TenantId, Guid.NewGuid());
        await using var db1 = first.Db;
        await using var db2 = second.Db;

        var results = await Task.WhenAll(
            CaptureAsync(() => first.Service.CreateAsync(Request(seed.AssetId, Slot, Slot.AddHours(1), "On A"), default)),
            CaptureAsync(() => second.Service.CreateAsync(Request(seed.SecondAssetId, Slot, Slot.AddHours(1), "On B"), default)));

        Assert.All(results, r => Assert.Null(r.Error));
    }

    [DockerFact]
    public async Task Concurrent_start_and_cancel_of_one_allocation_apply_exactly_one()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);
        var creator = Guid.NewGuid();
        var setup = Stack(factory, seed.TenantId, creator);
        await using var setupDb = setup.Db;
        var created = await setup.Service.CreateAsync(Request(seed.AssetId, Slot, Slot.AddHours(1), "Contested"), default);

        var a = Stack(factory, seed.TenantId, creator);
        var b = Stack(factory, seed.TenantId, creator);
        await using var db1 = a.Db;
        await using var db2 = b.Db;

        var results = await Task.WhenAll(
            CaptureAsync(() => a.Service.ApplyUsageAsync(
                created.Id, new AssetAllocationUsageRequest { Action = "start", ExpectedVersion = created.Version }, default)),
            CaptureAsync(() => b.Service.ApplyUsageAsync(
                created.Id, new AssetAllocationUsageRequest { Action = "cancel", ExpectedVersion = created.Version }, default)));

        Assert.Single(results, r => r.Error is null);
        var loser = Assert.IsType<PlanningException>(Assert.Single(results, r => r.Error is not null).Error);
        Assert.Equal(PlanningException.VersionConflictCode, loser.Code);
    }

    [DockerFact]
    public async Task Held_asset_lock_blocks_an_allocation_create_until_it_is_released()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);
        var holder = factory.Create(new FakeTenantProvider { TenantId = seed.TenantId });
        await using var holderDb = holder;
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await AssetRowLock.LockAsync(holder, seed.TenantId, [seed.AssetId], default);

        var writer = Stack(factory, seed.TenantId, Guid.NewGuid());
        await using var writerDb = writer.Db;
        var create = writer.Service.CreateAsync(Request(seed.AssetId, Slot, Slot.AddHours(1), "Waits"), default);

        Assert.False(await CompletesWithinAsync(create, TimeSpan.FromMilliseconds(1500)));

        await transaction.CommitAsync();
        var created = await create.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("Scheduled", created.Status);
    }

    [DockerFact]
    public async Task Held_asset_lock_blocks_the_hours_put_so_it_never_interleaves_with_an_allocation()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);
        var holder = factory.Create(new FakeTenantProvider { TenantId = seed.TenantId });
        await using var holderDb = holder;
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await AssetRowLock.LockAsync(holder, seed.TenantId, [seed.AssetId], default);

        var provider = new FakeTenantProvider { TenantId = seed.TenantId };
        await using var profileDb = factory.Create(provider);
        var profiles = new PlanningProfileService(
            profileDb, provider, new FixedModuleAccessor(PlatformModules.ResourcePlanning));
        var put = profiles.UpsertProfileAsync(
            seed.AssetId,
            new PutAssetPlanningProfileRequest
            {
                IsPlannable = true,
                HoursPolicy = "WithinHours",
                WorkingHours = [new AssetWorkingHoursWindowDto { DayOfWeek = 1, OpenTime = "08:00", CloseTime = "18:00" }],
            },
            default);

        Assert.False(await CompletesWithinAsync(put, TimeSpan.FromMilliseconds(1500)));

        await transaction.CommitAsync();
        var saved = await put.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("WithinHours", saved.HoursPolicy);
    }

    [DockerFact]
    public async Task Lock_of_one_tenant_id_does_not_touch_another_tenants_asset_row()
    {
        var factory = RequireFactory();
        var owner = await SeedAsync(factory);
        var foreign = await SeedAsync(factory);
        var holder = factory.Create(new FakeTenantProvider { TenantId = owner.TenantId });
        await using var holderDb = holder;
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await AssetRowLock.LockAsync(holder, owner.TenantId, [owner.AssetId], default);

        // The owner's asset id with the foreign tenant id matches no row: it returns at once.
        await using var other = factory.Create(new FakeTenantProvider { TenantId = foreign.TenantId });
        await using var otherTransaction = await other.Database.BeginTransactionAsync();
        var wrongTenant = AssetRowLock.LockAsync(other, foreign.TenantId, [owner.AssetId], default);
        Assert.True(await CompletesWithinAsync(wrongTenant, TimeSpan.FromSeconds(5)));

        // The foreign tenant's own asset is a different row, also free.
        var ownRow = AssetRowLock.LockAsync(other, foreign.TenantId, [foreign.AssetId], default);
        Assert.True(await CompletesWithinAsync(ownRow, TimeSpan.FromSeconds(5)));
    }

    [DockerFact]
    public async Task Agenda_reads_a_constant_number_of_times_whatever_the_number_of_assets_and_allocations()
    {
        var factory = RequireFactory();
        var small = await SeedAsync(factory);
        var large = await SeedAsync(factory);

        await using (var db = factory.Create(new FakeTenantProvider { TenantId = small.TenantId }))
        {
            db.AssetAllocations.Add(NewAllocation(small.TenantId, small.AssetId, small.UnitId, Slot, "One"));
            await db.SaveChangesAsync();
        }

        await using (var db = factory.Create(new FakeTenantProvider { TenantId = large.TenantId }))
        {
            var category = await db.AssetCategories.FirstAsync();
            var family = await db.AssetFamilies.FirstAsync();
            for (var i = 0; i < 12; i++)
            {
                var asset = PlanningTestData.NewAsset(large.TenantId, large.UnitId, category.Id, family.Id, $"Extra {i}", $"X{i}");
                db.Assets.Add(asset);
                db.AssetPlanningProfiles.Add(new AssetPlanningProfile
                {
                    TenantId = large.TenantId,
                    AssetId = asset.Id,
                    HoursPolicy = AssetHoursPolicy.WithinHours,
                });
                db.AssetWorkingHours.Add(PlanningTestData.Hours(large.TenantId, asset.Id, DayOfWeek.Monday, 8, 18));
                for (var j = 0; j < 5; j++)
                {
                    db.AssetAllocations.Add(NewAllocation(large.TenantId, asset.Id, large.UnitId, Slot.AddHours(j * 2), $"Job {i}-{j}"));
                }
            }

            await db.SaveChangesAsync();
        }

        var smallReads = await CountAgendaReadsAsync(factory, small.TenantId);
        var largeReads = await CountAgendaReadsAsync(factory, large.TenantId);

        Assert.Equal(smallReads.Reads, largeReads.Reads);
        Assert.Equal(1, smallReads.Items);
        Assert.Equal(60, largeReads.Items);
        Assert.Equal(14, largeReads.Resources);
        Assert.True(largeReads.Reads <= 6, $"The agenda made {largeReads.Reads} reads.");
    }

    [Fact]
    public void Grant_migration_is_insert_only_idempotent_and_its_down_is_a_guarded_no_op()
    {
        var migration = new GrantResourcePlanningPermissions();

        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Single(migration.UpOperations);
        Assert.Empty(migration.DownOperations);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(up, "ON CONFLICT").Count);
        foreach (var key in new[]
        {
            Permissions.ResourcePlanning.AgendaRead,
            Permissions.ResourcePlanning.AllocationsManage,
            Permissions.ResourcePlanning.AllocationsExecute,
            Permissions.ResourcePlanning.AvailabilityWrite,
        })
        {
            Assert.Contains($"'{key}'", up, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[] { "DELETE ", "UPDATE ", "DROP ", "TRUNCATE", "ALTER " })
        {
            Assert.DoesNotContain(forbidden, up, StringComparison.OrdinalIgnoreCase);
        }

        // The technician grant is exactly the two keys of TechnicianLegacyKeys.
        Assert.Equal(
            PermissionCatalog.TechnicianLegacyKeys.Where(k => k.StartsWith("resource-planning.", StringComparison.Ordinal)).Order().ToArray(),
            new[] { Permissions.ResourcePlanning.AgendaRead, Permissions.ResourcePlanning.AllocationsExecute });
    }

    [DockerFact]
    public async Task Grant_migration_grants_existing_technician_and_admin_roles_and_is_idempotent()
    {
        var factory = RequireFactory();
        var seed = await SeedAsync(factory);
        Guid technicianId;
        Guid adminId;
        Guid userId;
        Guid customId;
        await using (var db = factory.Create(new FakeTenantProvider { TenantId = seed.TenantId }))
        {
            var technician = new Role(seed.TenantId, SystemRoles.Technician, "Technician (system)", isSystemRole: true);
            var admin = new Role(seed.TenantId, SystemRoles.Admin, "Admin (system)", isSystemRole: true);
            var user = new Role(seed.TenantId, SystemRoles.User, "User (system)", isSystemRole: true);
            var custom = new Role(seed.TenantId, "Shift lead");
            db.Roles.AddRange(technician, admin, user, custom);
            await db.SaveChangesAsync();
            (technicianId, adminId, userId, customId) = (technician.Id, admin.Id, user.Id, custom.Id);
        }

        var sql = Assert.Single(new GrantResourcePlanningPermissions().UpOperations.OfType<SqlOperation>()).Sql;
        for (var run = 0; run < 2; run++)
        {
            await using var db = factory.Create(new FakeTenantProvider());
            await db.Database.ExecuteSqlRawAsync(sql);
        }

        await using var verify = factory.Create(new FakeTenantProvider());
        async Task<string[]> KeysAsync(Guid roleId) =>
            (await verify.RolePermissions
                .Where(rp => rp.RoleId == roleId && rp.Permission.Key.StartsWith("resource-planning."))
                .Select(rp => rp.Permission.Key)
                .ToListAsync()).Order().ToArray();

        Assert.Equal(
            new[] { Permissions.ResourcePlanning.AgendaRead, Permissions.ResourcePlanning.AllocationsExecute },
            await KeysAsync(technicianId));
        Assert.Equal(
            new[]
            {
                Permissions.ResourcePlanning.AgendaRead,
                Permissions.ResourcePlanning.AllocationsExecute,
                Permissions.ResourcePlanning.AllocationsManage,
                Permissions.ResourcePlanning.AvailabilityWrite,
            }.Order().ToArray(),
            await KeysAsync(adminId));
        Assert.Empty(await KeysAsync(userId));
        Assert.Empty(await KeysAsync(customId));
    }

    private static async Task<AgendaReads> CountAgendaReadsAsync(PostgresAppDbFactory factory, Guid tenantId)
    {
        var provider = new FakeTenantProvider { TenantId = tenantId };
        var counter = new ReadCounter();
        await using var db = factory.Create(provider, counter);
        var service = new PlanningAgendaService(
            db, provider, new FixedModuleAccessor(PlatformModules.ResourcePlanning), TimeProvider.System);

        var agenda = await service.GetAgendaAsync(Slot.AddDays(-1), Slot.AddDays(6), null, null, default);

        return new AgendaReads(counter.Reads, agenda.Resources.Count, agenda.Items.Count);
    }

    private static AssetAllocation NewAllocation(Guid tenantId, Guid assetId, Guid unitId, DateTimeOffset start, string title) =>
        new()
        {
            TenantId = tenantId,
            AssetId = assetId,
            UnitId = unitId,
            Title = title,
            PlannedWorkStart = start,
            PlannedWorkEnd = start.AddHours(1),
            OccupiedStart = start,
            OccupiedEnd = start.AddHours(1),
            TimeZoneId = "America/Sao_Paulo",
        };

    private static CreateAssetAllocationRequest Request(Guid assetId, DateTimeOffset start, DateTimeOffset end, string title) =>
        new() { AssetId = assetId, Title = title, WorkStart = start, WorkEnd = end };

    private static (AssetAllocationService Service, AppDbContext Db) Stack(
        PostgresAppDbFactory factory,
        Guid tenantId,
        Guid userId)
    {
        var provider = new FakeTenantProvider { TenantId = tenantId };
        var db = factory.Create(provider);
        var occupancy = new AssetOccupancyService(
            db, new FixedModuleAccessor(PlatformModules.ResourcePlanning), TimeProvider.System);
        var service = new AssetAllocationService(
            db, provider, occupancy, new FakePlanningCaller(userId, canManage: false), TimeProvider.System);
        return (service, db);
    }

    private static async Task<PlanningTenantSeed> SeedAsync(PostgresAppDbFactory factory)
    {
        await using var db = factory.Create(new FakeTenantProvider());
        var seed = PlanningTestData.AddTenant(db);
        await db.SaveChangesAsync();
        return seed;
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout) =>
        await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory!;
    }

    private static async Task<Capture<T>> CaptureAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return new Capture<T>(await action(), null);
        }
        catch (Exception ex)
        {
            return new Capture<T>(default, ex);
        }
    }

    private sealed record Capture<T>(T? Value, Exception? Error);

    private sealed record AgendaReads(int Reads, int Resources, int Items);

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult(result);
        }
    }
}
