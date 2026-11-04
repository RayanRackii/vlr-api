using Platform.Api.Storage;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog;

/// <summary>
/// Where a catalog product file lives. Only customer-visible files of Public products sit in the
/// public bucket (permanent URL). Internal files and every file of a Restricted product live in the
/// private bucket and are served through short-lived signed URLs after authorization.
/// </summary>
public static class CatalogFileBuckets
{
    public static bool IsPublic(CatalogProductVisibility product, CatalogFileVisibility file) =>
        file == CatalogFileVisibility.CustomerVisible && product == CatalogProductVisibility.Public;

    public static string For(StorageOptions options, CatalogProductVisibility product, CatalogFileVisibility file) =>
        IsPublic(product, file) ? options.PublicBucket : options.PrivateBucket;
}
