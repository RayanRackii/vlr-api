using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Tests.Rentals;

public sealed class FiccTeacherLessonScopeTests
{
    private static readonly FiccTeacherLessonScope Scope = new();

    [Fact]
    public void Allows_only_the_six_configured_FICC_rental_assets()
    {
        Guid[] allowed =
        [
            Guid.Parse("98259729-fa57-4b5a-abcf-1d36603fc96e"),
            Guid.Parse("edb91e39-e03d-42f5-b38a-518a5427344b"),
            Guid.Parse("35fa8602-f7c1-4876-9b18-dd1f8e8a546b"),
            Guid.Parse("04721eb7-d6c2-4197-9930-a8874de5ad27"),
            Guid.Parse("4f586a63-a89e-467b-b139-a3125a2ba9ec"),
            Guid.Parse("32b9bd39-9e32-4f25-ad6d-67117c882a3c"),
        ];

        Assert.Equal(6, allowed.Distinct().Count());
        Assert.All(allowed, id => Assert.True(Scope.Allows(FiccTeacherLessonScope.TenantId, id)));
        Assert.False(Scope.Allows(FiccTeacherLessonScope.TenantId, Guid.NewGuid()));
        Assert.False(Scope.Allows(Guid.NewGuid(), allowed[0]));
    }
}

internal sealed class TestTeacherLessonScope : ITeacherLessonScope
{
    public bool Allows(Guid tenantId, Guid rentalAssetId) => true;
}
