namespace Platform.Core.Domain.Exceptions;

public sealed class CatalogModuleInactiveException : InvalidOperationException
{
    public CatalogModuleInactiveException()
        : base("Catalog module is not active for this tenant.")
    {
    }
}
