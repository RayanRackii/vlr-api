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

    Task VerifyEmailCodeAsync(
        Customer customer,
        string code,
        CancellationToken cancellationToken);
}
