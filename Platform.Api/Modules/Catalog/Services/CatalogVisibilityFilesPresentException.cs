namespace Platform.Api.Modules.Catalog.Services;

public sealed class CatalogVisibilityFilesPresentException()
    : InvalidOperationException("Remove or re-upload legacy public files before changing visibility to Restricted.")
{
    public const string Code = "catalog.visibility.filesPresent";
}
