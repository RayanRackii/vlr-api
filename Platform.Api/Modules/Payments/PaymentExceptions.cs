namespace Platform.Api.Modules.Payments;

/// <summary>
/// Payments are not configured correctly (missing or invalid key, option or secret). Thrown at use time, never at startup,
/// so environments without payments keep booting. The message never contains a secret value.
/// </summary>
public sealed class PaymentConfigurationException : Exception
{
    public PaymentConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The payment provider rejected a call or could not be reached. Carries only the provider error code and the HTTP status
/// (0 when no response arrived), never the response body, the request body or any key.
/// </summary>
public sealed class PaymentGatewayException : Exception
{
    public PaymentGatewayException(string code, int httpStatus)
        : base($"Payment gateway request failed (HTTP {httpStatus}, code {code}).")
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public string Code { get; }

    public int HttpStatus { get; }
}
