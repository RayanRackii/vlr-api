namespace Platform.Core.Domain.Exceptions;

public sealed class InvalidCatalogOrderTransitionException : InvalidOperationException
{
    public InvalidCatalogOrderTransitionException(string message)
        : base(message)
    {
    }
}
