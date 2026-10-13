using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.CustomerAuth.PhoneVerification;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.CustomerAuth.Services;

public sealed class CustomerVerificationCodeService(
    AppDbContext dbContext,
    IConfiguration configuration,
    TimeProvider timeProvider) : ICustomerVerificationCodeService
{
    public const int MaxAttempts = 5;

    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan PasswordResetTimeToLive = TimeSpan.FromMinutes(15);

    public Task<string> IssueEmailVerificationAsync(
        Customer customer,
        CancellationToken cancellationToken) =>
        IssueAsync(
            customer,
            OtpPurposes.EmailVerification,
            TimeToLive,
            cancellationToken);

    public Task VerifyEmailCodeAsync(
        Customer customer,
        string code,
        CancellationToken cancellationToken) =>
        VerifyAsync(customer, OtpPurposes.EmailVerification, code, cancellationToken);

    public Task<string> IssuePasswordResetAsync(
        Customer customer,
        CancellationToken cancellationToken) =>
        IssueAsync(
            customer,
            OtpPurposes.PasswordReset,
            PasswordResetTimeToLive,
            cancellationToken);

    public Task VerifyPasswordResetCodeAsync(
        Customer customer,
        string code,
        CancellationToken cancellationToken) =>
        VerifyAsync(customer, OtpPurposes.PasswordReset, code, cancellationToken);

    private async Task<string> IssueAsync(
        Customer customer,
        string purpose,
        TimeSpan timeToLive,
        CancellationToken cancellationToken)
    {
        var secret = RequireJwtSecret();
        var now = timeProvider.GetUtcNow();

        var prior = await dbContext.OtpCodes
            .Where(o =>
                o.CustomerId == customer.Id
                && o.Purpose == purpose
                && !o.IsUsed
                && o.ReplacedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var row in prior)
        {
            row.ReplacedAt = now;
            row.Touch();
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var otp = new OtpCode
        {
            TenantId = customer.TenantId,
            CustomerId = customer.Id,
            Code = null,
            Purpose = purpose,
            CodeHash = Convert.ToBase64String(ComputeHash(secret, customer.Id, purpose, code)),
            Attempts = 0,
            ExpiresAt = now.Add(timeToLive),
            IsUsed = false,
        };

        dbContext.OtpCodes.Add(otp);
        await dbContext.SaveChangesAsync(cancellationToken);
        return code;
    }

    private async Task VerifyAsync(
        Customer customer,
        string purpose,
        string code,
        CancellationToken cancellationToken)
    {
        var secret = RequireJwtSecret();
        var now = timeProvider.GetUtcNow();

        var otp = await dbContext.OtpCodes
            .Where(o =>
                o.CustomerId == customer.Id
                && o.Purpose == purpose
                && !o.IsUsed
                && o.ReplacedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (otp is null || otp.ExpiresAt <= now || otp.Attempts >= MaxAttempts)
        {
            throw InvalidCode();
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(otp.CodeHash ?? string.Empty);
        }
        catch (FormatException)
        {
            throw InvalidCode();
        }

        var actual = ComputeHash(secret, customer.Id, purpose, code);
        if (expected.Length != actual.Length
            || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            otp.Attempts++;
            otp.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);
            throw InvalidCode();
        }

        otp.MarkAsUsed();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private string RequireJwtSecret() =>
        configuration["Supabase:JwtSecret"]
        ?? throw new InvalidOperationException("Supabase:JwtSecret is not configured.");

    private static byte[] ComputeHash(
        string secret,
        Guid customerId,
        string purpose,
        string code)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var payload = Encoding.UTF8.GetBytes($"{customerId:N}:{purpose}:{code}");
        return HMACSHA256.HashData(key, payload);
    }

    private static PhoneVerificationInvalidException InvalidCode() =>
        new(TwilioVerifyPhoneVerificationClient.InvalidOrExpiredMessage);
}
