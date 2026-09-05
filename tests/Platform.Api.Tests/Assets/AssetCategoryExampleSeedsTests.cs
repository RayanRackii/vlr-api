using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Assets;

public sealed class AssetCategoryExampleSeedsTests
{
    [Fact]
    public void Generic_cannot_provision_an_example_category()
    {
        Assert.False(AssetCategoryExampleSeeds.CanProvisionExampleCategory(AssetFamilyKeys.Generic));
        Assert.False(AssetCategoryExampleSeeds.ByFamilyKey.ContainsKey(AssetFamilyKeys.Generic));
    }

    [Theory]
    [InlineData(AssetFamilyKeys.Spaces, "Quadra")]
    [InlineData(AssetFamilyKeys.Electrical, "Quadro elétrico")]
    [InlineData(AssetFamilyKeys.Goods, "Caçamba")]
    public void Provisioning_families_map_to_the_canonical_example_names(string familyKey, string expectedName)
    {
        Assert.True(AssetCategoryExampleSeeds.CanProvisionExampleCategory(familyKey));
        Assert.Equal(expectedName, AssetCategoryExampleSeeds.ByFamilyKey[familyKey]);
    }

    [Fact]
    public void HasPmocProvisioningFamily_is_false_for_generic_only()
    {
        Assert.False(AssetCategoryExampleSeeds.HasPmocProvisioningFamily([AssetFamilyKeys.Generic]));
    }

    [Theory]
    [InlineData(AssetFamilyKeys.Spaces)]
    [InlineData(AssetFamilyKeys.Electrical)]
    [InlineData(AssetFamilyKeys.Goods)]
    public void HasPmocProvisioningFamily_is_true_when_a_seed_family_is_present(string familyKey)
    {
        Assert.True(AssetCategoryExampleSeeds.HasPmocProvisioningFamily([AssetFamilyKeys.Generic, familyKey]));
    }
}
