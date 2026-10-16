namespace Platform.Api.Modules.Rentals;

/// <summary>
/// A booking or lesson write named a start instant that is already in the past
/// on the tenant clock (<see cref="Services.TenantBookingClock"/>).
/// </summary>
public sealed class StartInThePastException : ArgumentException
{
    public const string Code = "START_IN_THE_PAST";

    public StartInThePastException(string message)
        : base(message)
    {
    }
}
