namespace Platform.Api.Modules.Catalog;

public static class CatalogFileUrls
{
    // Bound the revocation window after product restriction or grant removal.
    public static readonly TimeSpan SignedUrlTtl = TimeSpan.FromSeconds(120);
}
