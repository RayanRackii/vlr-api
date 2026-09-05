using System.Reflection;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogAssetIsolationTests
{
    [Fact]
    public void Catalog_module_types_do_not_depend_on_asset_registry()
    {
        var assetType = typeof(Asset);
        var assetCategoryType = typeof(AssetCategory);
        var catalogTypes = typeof(CatalogProductService).Assembly.GetTypes()
            .Where(type => type.Namespace is not null
                && type.Namespace.StartsWith("Platform.Api.Modules.Catalog", StringComparison.Ordinal));

        foreach (var type in catalogTypes)
        {
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    Assert.False(
                        IsAssetRegistryType(parameter.ParameterType, assetType, assetCategoryType),
                        $"{type.Name} constructor depends on {parameter.ParameterType.Name}");
                }
            }

            foreach (var field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.False(
                    IsAssetRegistryType(field.FieldType, assetType, assetCategoryType),
                    $"{type.Name}.{field.Name} is {field.FieldType.Name}");
            }
        }
    }

    private static bool IsAssetRegistryType(Type type, Type assetType, Type assetCategoryType)
    {
        if (type == assetType || type == assetCategoryType)
        {
            return true;
        }

        var name = type.Name;
        return name is "IAssetRegistry" or "IAssetService" or "IAssetCategoryService" or "IAssetFamilyService";
    }
}
