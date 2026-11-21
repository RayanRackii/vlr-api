using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Api.Services.Tenancy;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.Rentals;

/// <summary>
/// The data migration that carries the tenant and rental asset ids once written in code
/// (<c>FiccTeacherLessonScope</c>) and in appsettings into tenant settings. These ids may appear
/// here and in the migration, and nowhere else.
/// </summary>
public sealed class TenantSettingsSeedMigrationTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    private static readonly Guid ProdTenantId = Guid.Parse("3ae86b53-d43e-42b0-9616-4833d02f7a1e");
    private static readonly Guid DevTenantId = Guid.Parse("b27c9a0a-d585-4626-8c13-1468c5a43519");

    private static readonly Guid[] ProdRentalAssetIds =
    [
        Guid.Parse("98259729-fa57-4b5a-abcf-1d36603fc96e"),
        Guid.Parse("edb91e39-e03d-42f5-b38a-518a5427344b"),
        Guid.Parse("35fa8602-f7c1-4876-9b18-dd1f8e8a546b"),
        Guid.Parse("04721eb7-d6c2-4197-9930-a8874de5ad27"),
        Guid.Parse("4f586a63-a89e-467b-b139-a3125a2ba9ec"),
        Guid.Parse("32b9bd39-9e32-4f25-ad6d-67117c882a3c"),
    ];

    private static readonly Guid[] DevRentalAssetIds =
    [
        Guid.Parse("e82cbc28-0fdc-4d44-a062-2efede0f16b8"),
        Guid.Parse("e56e4f22-f45e-486d-a80b-a3714d6e3d5e"),
    ];

    private static string UpSql() =>
        Assert.Single(new SeedTenantSettingsForTeacherLessonsAndBookingUi().UpOperations.OfType<SqlOperation>()).Sql;

    [Fact]
    public void Migration_is_data_only_insert_only_guarded_and_after_the_idempotency_keys_migration()
    {
        var migration = new SeedTenantSettingsForTeacherLessonsAndBookingUi();
        Assert.Single(migration.UpOperations);
        Assert.Empty(migration.DownOperations);

        var sql = UpSql();
        Assert.Contains("WHERE EXISTS (SELECT 1 FROM core.tenants", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (tenant_id, key) DO NOTHING", sql, StringComparison.Ordinal);
        Assert.Contains(TenantSettingKeys.TeacherLessonRentalAssetIds, sql, StringComparison.Ordinal);
        Assert.Contains(TenantSettingKeys.BookingUiEnabled, sql, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "DELETE ", "UPDATE ", "DROP ", "TRUNCATE", "ALTER ", "CREATE " })
        {
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        }

        var id = typeof(SeedTenantSettingsForTeacherLessonsAndBookingUi)
            .GetCustomAttribute<MigrationAttribute>()!.Id;
        Assert.True(
            string.CompareOrdinal(id, "20261128100000_AddIdempotencyKeys") > 0,
            $"Migration {id} must sort after 20261128100000_AddIdempotencyKeys.");
    }

    [DockerFact]
    public async Task Inserts_only_for_existing_tenants_never_overwrites_and_is_idempotent()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        await using var db = factory.Create(new FakeTenantProvider());
        var sql = UpSql();

        await AddTenantAsync(db, ProdTenantId);
        await AddTenantAsync(db, Guid.NewGuid());
        await db.Database.ExecuteSqlRawAsync(sql);

        var rows = await RowsAsync(db);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(ProdTenantId, row.TenantId));
        var lessons = Assert.Single(rows, row => row.Key == TenantSettingKeys.TeacherLessonRentalAssetIds);
        Assert.True(TenantSettingValues.TryParseRentalAssetIds(lessons.Value, out var parsed));
        Assert.Equal(ProdRentalAssetIds.Order(), parsed.Order());
        Assert.Equal(lessons.Value, TenantSettingValues.FormatRentalAssetIds(ProdRentalAssetIds));
        Assert.Equal("true", Assert.Single(rows, row => row.Key == TenantSettingKeys.BookingUiEnabled).Value);

        // A value an admin set afterwards survives a second run (and the row count does not grow).
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE core.tenant_settings SET value = 'false' WHERE tenant_id = {ProdTenantId} AND key = {TenantSettingKeys.BookingUiEnabled}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE core.tenant_settings SET value = '[]' WHERE tenant_id = {ProdTenantId} AND key = {TenantSettingKeys.TeacherLessonRentalAssetIds}
            """);
        await db.Database.ExecuteSqlRawAsync(sql);
        var again = await RowsAsync(db);
        Assert.Equal(2, again.Count);
        Assert.Equal("false", again.Single(row => row.Key == TenantSettingKeys.BookingUiEnabled).Value);
        Assert.Equal("[]", again.Single(row => row.Key == TenantSettingKeys.TeacherLessonRentalAssetIds).Value);

        // The other hard-coded tenant is picked up once it exists; the first one is untouched.
        await AddTenantAsync(db, DevTenantId);
        await db.Database.ExecuteSqlRawAsync(sql);
        var final = await RowsAsync(db);
        Assert.Equal(4, final.Count);
        Assert.Equal(2, final.Count(row => row.TenantId == DevTenantId));
        var devLessons = final.Single(row =>
            row.TenantId == DevTenantId && row.Key == TenantSettingKeys.TeacherLessonRentalAssetIds);
        Assert.True(TenantSettingValues.TryParseRentalAssetIds(devLessons.Value, out var devParsed));
        Assert.Equal(DevRentalAssetIds.Order(), devParsed.Order());
        Assert.Equal("true", final.Single(row =>
            row.TenantId == DevTenantId && row.Key == TenantSettingKeys.BookingUiEnabled).Value);
        Assert.Equal("false", final.Single(row =>
            row.TenantId == ProdTenantId && row.Key == TenantSettingKeys.BookingUiEnabled).Value);
    }

    private static async Task AddTenantAsync(AppDbContext db, Guid tenantId)
    {
        var tenant = new Tenant(
            "Seed test",
            $"{Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999)}",
            subdomain: $"seed-{Guid.NewGuid():N}"[..16]);
        db.Entry(tenant).Property(entity => entity.Id).CurrentValue = tenantId;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
    }

    private static Task<List<TenantSetting>> RowsAsync(AppDbContext db) =>
        db.TenantSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(setting => setting.Key == TenantSettingKeys.TeacherLessonRentalAssetIds
                || setting.Key == TenantSettingKeys.BookingUiEnabled)
            .ToListAsync();
}
