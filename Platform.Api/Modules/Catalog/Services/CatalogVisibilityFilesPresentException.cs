namespace Platform.Api.Modules.Catalog.Services;

public sealed class CatalogVisibilityFilesPresentException()
    : InvalidOperationException("Remove the product files before changing visibility, then re-upload them if needed.")
{
    public const string Code = "catalog.visibility.filesPresent";
}
