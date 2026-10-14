using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Tests.Rentals;

public sealed class FiccTeacherLessonScopeTests
{
    private static readonly FiccTeacherLessonScope Scope = new();

    [Fact]
    public void Allows_reviewed_production_and_dev_rental_assets_only()
    {
        Guid[] production =
        [
            Guid.Parse("98259729-fa57-4b5a-abcf-1d36603fc96e"),
            Guid.Parse("edb91e39-e03d-42f5-b38a-518a5427344b"),
            Guid.Parse("35fa8602-f7c1-4876-9b18-dd1f8e8a546b"),
            Guid.Parse("04721eb7-d6c2-4197-9930-a8874de5ad27"),
            Guid.Parse("4f586a63-a89e-467b-b139-a3125a2ba9ec"),
            Guid.Parse("32b9bd39-9e32-4f25-ad6d-67117c882a3c"),
        ];

        Assert.Equal(6, production.Distinct().Count());
        Assert.All(production, id => Assert.True(Scope.Allows(FiccTeacherLessonScope.TenantId, id)));
        Assert.False(Scope.Allows(FiccTeacherLessonScope.TenantId, Guid.NewGuid()));
        Assert.False(Scope.Allows(Guid.NewGuid(), production[0]));
        Assert.False(Scope.Allows(FiccTeacherLessonScope.DevTenantId, production[0]));

        Assert.True(Scope.Allows(
            FiccTeacherLessonScope.DevTenantId,
            FiccTeacherLessonScope.DevQuadra1RentalAssetId));
        Assert.True(Scope.Allows(
            FiccTeacherLessonScope.DevTenantId,
            FiccTeacherLessonScope.DevQuadra2RentalAssetId));
        Assert.False(Scope.Allows(
            FiccTeacherLessonScope.DevTenantId,
            FiccTeacherLessonScope.DevQuadra1AssetId));
        Assert.False(Scope.Allows(
            FiccTeacherLessonScope.DevTenantId,
            FiccTeacherLessonScope.DevQuadra2AssetId));
        Assert.False(Scope.Allows(
            FiccTeacherLessonScope.TenantId,
            FiccTeacherLessonScope.DevQuadra1RentalAssetId));
        Assert.False(Scope.Allows(FiccTeacherLessonScope.DevTenantId, Guid.NewGuid()));
    }
}

internal sealed class TestTeacherLessonScope : ITeacherLessonScope
{
    public bool Allows(Guid tenantId, Guid rentalAssetId) => true;
}
