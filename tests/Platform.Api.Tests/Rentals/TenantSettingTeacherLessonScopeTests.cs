using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Services.Tenancy;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class TenantSettingTeacherLessonScopeTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();
    private static readonly Guid RentalAssetOne = Guid.NewGuid();
    private static readonly Guid RentalAssetTwo = Guid.NewGuid();

    private static AppDbContext NewDb(Guid? ambientTenant = null) =>
        InMemoryAppDb.Create(
            new FakeTenantProvider { TenantId = ambientTenant },
            $"lesson-scope-{Guid.NewGuid():N}");

    private static async Task SetAsync(AppDbContext db, Guid tenantId, string value)
    {
        db.TenantSettings.Add(new TenantSetting
        {
            TenantId = tenantId,
            Key = TenantSettingKeys.TeacherLessonRentalAssetIds,
            Value = value,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Without_the_setting_no_rental_asset_is_allowed()
    {
        await using var db = NewDb();
        var scope = new TenantSettingTeacherLessonScope(db);

        Assert.False(await scope.AllowsAsync(TenantA, RentalAssetOne, CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_listed_rental_assets_of_the_tenant_are_allowed()
    {
        await using var db = NewDb();
        await SetAsync(db, TenantA, TenantSettingValues.FormatRentalAssetIds([RentalAssetOne]));
        var scope = new TenantSettingTeacherLessonScope(db);

        Assert.True(await scope.AllowsAsync(TenantA, RentalAssetOne, CancellationToken.None));
        Assert.False(await scope.AllowsAsync(TenantA, RentalAssetTwo, CancellationToken.None));
        Assert.False(await scope.AllowsAsync(TenantA, Guid.Empty, CancellationToken.None));
        // Another tenant has no setting, so the same id is refused there.
        Assert.False(await scope.AllowsAsync(TenantB, RentalAssetOne, CancellationToken.None));
        Assert.False(await scope.AllowsAsync(Guid.Empty, RentalAssetOne, CancellationToken.None));
    }

    [Fact]
    public async Task Each_tenant_is_scoped_by_its_own_setting_whatever_the_ambient_tenant_is()
    {
        await using var db = NewDb(ambientTenant: TenantB);
        await SetAsync(db, TenantA, TenantSettingValues.FormatRentalAssetIds([RentalAssetOne]));
        await SetAsync(db, TenantB, TenantSettingValues.FormatRentalAssetIds([RentalAssetTwo]));
        var scope = new TenantSettingTeacherLessonScope(db);

        Assert.True(await scope.AllowsAsync(TenantA, RentalAssetOne, CancellationToken.None));
        Assert.False(await scope.AllowsAsync(TenantA, RentalAssetTwo, CancellationToken.None));
        Assert.True(await scope.AllowsAsync(TenantB, RentalAssetTwo, CancellationToken.None));
        Assert.False(await scope.AllowsAsync(TenantB, RentalAssetOne, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("\"98259729-fa57-4b5a-abcf-1d36603fc96e\"")]
    [InlineData("[1, 2]")]
    [InlineData("[\"not-a-guid\"]")]
    [InlineData("[\"00000000-0000-0000-0000-000000000000\"]")]
    public async Task Empty_or_malformed_values_disable_lessons(string value)
    {
        await using var db = NewDb();
        await SetAsync(db, TenantA, value);
        var scope = new TenantSettingTeacherLessonScope(db);

        Assert.False(await scope.AllowsAsync(TenantA, RentalAssetOne, CancellationToken.None));
    }

    [Fact]
    public async Task One_bad_element_disables_the_whole_list()
    {
        await using var db = NewDb();
        await SetAsync(db, TenantA, $"[\"{RentalAssetOne}\", \"oops\"]");
        var scope = new TenantSettingTeacherLessonScope(db);

        Assert.False(await scope.AllowsAsync(TenantA, RentalAssetOne, CancellationToken.None));
    }

    [Fact]
    public async Task The_setting_is_read_once_per_scope_instance()
    {
        await using var db = NewDb();
        await SetAsync(db, TenantA, TenantSettingValues.FormatRentalAssetIds([RentalAssetOne]));
        var scope = new TenantSettingTeacherLessonScope(db);
        Assert.True(await scope.AllowsAsync(TenantA, RentalAssetOne, CancellationToken.None));

        var row = db.TenantSettings.Single(setting => setting.TenantId == TenantA);
        row.Value = "[]";
        await db.SaveChangesAsync();

        // Same request (same instance): cached. A new request (new instance) sees the change.
        Assert.True(await scope.AllowsAsync(TenantA, RentalAssetOne, CancellationToken.None));
        Assert.False(await new TenantSettingTeacherLessonScope(db)
            .AllowsAsync(TenantA, RentalAssetOne, CancellationToken.None));
    }
}

internal sealed class TestTeacherLessonScope : ITeacherLessonScope
{
    public Task<bool> AllowsAsync(Guid tenantId, Guid rentalAssetId, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
