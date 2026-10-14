namespace Platform.Api.Features.CreateTenant;

public interface IAnonymousTrialCreateGate
{
    /// <summary>
    /// Consumes one attempt in the caller IP window. Returns false when the window is full
    /// and does not record that rejected attempt.
    /// </summary>
    bool TryConsume(string? clientIp);
}
