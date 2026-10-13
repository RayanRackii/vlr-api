using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.CustomerAuth.Services;

public interface ICustomerVerificationCodeService
{
    /// <summary>
    /// Invalidates prior unused email-verification codes and returns the plaintext digits once.
    /// </summary>
    Task<string> IssueEmailVerificationAsync(
        Customer customer,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks the email code and, on success, runs <paramref name="onAccepted"/> in the same transaction.
    /// </summary>
    Task VerifyEmailCodeAsync(
        Customer customer,
        string code,
        Action<DateTimeOffset> onAccepted,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invalidates prior unused password-reset codes and returns the plaintext digits once.
    /// </summary>
    Task<string> IssuePasswordResetAsync(
        Customer customer,
        CancellationToken cancellationToken);

    /// <summary>
    /// Checks the reset code and, on success, runs <paramref name="onAccepted"/> in the same transaction.
    /// </summary>
    Task VerifyPasswordResetCodeAsync(
        Customer customer,
        string code,
        Action<DateTimeOffset> onAccepted,
        CancellationToken cancellationToken);
}
