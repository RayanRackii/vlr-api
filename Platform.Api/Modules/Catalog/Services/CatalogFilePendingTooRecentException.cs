namespace Platform.Api.Modules.Catalog.Services;

public sealed class CatalogFilePendingTooRecentException(int waitMinutes)
    : InvalidOperationException($"Please wait {waitMinutes} minute(s) before removing this incomplete file; its upload may still be completing.")
{
    public const string Code = "catalog.file.pendingTooRecent";
}
