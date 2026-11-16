namespace Platform.Api.Modules.Rentals;

/// <summary>
/// Staff tried to create a customer whose phone or e-mail already belongs to a customer of the
/// tenant. Carries the existing id so the client can select it instead.
/// </summary>
public sealed class CustomerAlreadyExistsException(Guid customerId)
    : InvalidOperationException("A customer with this phone or e-mail already exists.")
{
    public const string Code = "CUSTOMER_EXISTS";

    public Guid CustomerId { get; } = customerId;
}
