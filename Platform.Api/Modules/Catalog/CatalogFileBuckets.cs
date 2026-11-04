using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog;

/// <summary>New files are private. Legacy files retain their recorded storage location.</summary>
public static class CatalogFileBuckets
{
    public static bool IsPublic(CatalogProductVisibility product, CatalogProductFile file) =>
        file.IsLegacyPublic && product == CatalogProductVisibility.Public;

    public static string For(StorageOptions options, CatalogProductFile file) =>
        file.IsLegacyPublic ? options.PublicBucket : options.PrivateBucket;
}
