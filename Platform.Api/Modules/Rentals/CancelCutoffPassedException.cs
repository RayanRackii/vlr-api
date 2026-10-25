namespace Platform.Api.Modules.Rentals;

/// <summary>The effective customer cancellation deadline has been reached.</summary>
public sealed class CancelCutoffPassedException(int cancelCutoffHours)
    : InvalidOperationException("The cancellation deadline has passed.")
{
    public const string Code = "cancel_cutoff_passed";

    public int CancelCutoffHours { get; } = cancelCutoffHours;
}
