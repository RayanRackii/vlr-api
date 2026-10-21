namespace Platform.Api.Modules.Rentals;

public sealed class AvailabilityRequestException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
