namespace Platform.Api.Authentication;

/// <summary>
/// The customer session stamp could not be read. This is an infrastructure
/// failure, not an expired session: the pipeline exception handler returns HTTP 500.
/// The message is fixed and the inner exception is omitted so logs and the
/// response cannot carry tokens, connection strings, or other request data.
/// </summary>
internal sealed class CustomerSessionStoreUnavailableException : Exception
{
    public CustomerSessionStoreUnavailableException()
        : base("Customer session stamp check failed.")
    {
    }
}
