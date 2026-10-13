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
    TimeProvider timeProvider,
    OtpHmacEvaluationCounter? hmacEvaluations = null) : ICustomerVerificationCodeService
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
        Action<DateTimeOffset> onAccepted,
        CancellationToken cancellationToken) =>
        VerifyAsync(
            customer,
            OtpPurposes.EmailVerification,
            code,
            onAccepted,
            cancellationToken);

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
        Action<DateTimeOffset> onAccepted,
        CancellationToken cancellationToken) =>
        VerifyAsync(
            customer,
            OtpPurposes.PasswordReset,
            code,
            onAccepted,
            cancellationToken);

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
        Action<DateTimeOffset> onAccepted,
        CancellationToken cancellationToken)
    {
        var secret = RequireJwtSecret();
        var now = timeProvider.GetUtcNow();
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());

        if (dbContext.Database.IsRelational())
        {
            await VerifyRelationalAsync(
                customer,
                purpose,
                code,
                secret,
                now,
                stamp,
                onAccepted,
                cancellationToken);
            return;
        }

        await VerifyTrackedAsync(
            customer,
            purpose,
            code,
            secret,
            now,
            stamp,
            onAccepted,
            cancellationToken);
    }

    private async Task VerifyRelationalAsync(
        Customer customer,
        string purpose,
        string code,
        string secret,
        DateTimeOffset now,
        DateTimeOffset stamp,
        Action<DateTimeOffset> onAccepted,
        CancellationToken cancellationToken)
    {
        var otp = await dbContext.OtpCodes
            .AsNoTracking()
            .Where(o =>
                o.CustomerId == customer.Id
                && o.Purpose == purpose
                && !o.IsUsed
                && o.ReplacedAt == null)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Select(o => new { o.Id, o.CodeHash })
            .FirstOrDefaultAsync(cancellationToken);

        if (otp is null)
        {
            throw InvalidCode();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var incremented = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE core.otp_codes
            SET attempts = attempts + 1
            WHERE id = {otp.Id}
              AND tenant_id = {customer.TenantId}
              AND customer_id = {customer.Id}
              AND purpose = {purpose}
              AND attempts < {MaxAttempts}
              AND NOT is_used
              AND replaced_at IS NULL
              AND expires_at > {now}
            """,
            cancellationToken);

        if (incremented == 0)
        {
            throw InvalidCode();
        }

        hmacEvaluations?.Increment();
        if (!HashMatches(secret, customer.Id, purpose, code, otp.CodeHash))
        {
            await transaction.CommitAsync(cancellationToken);
            throw InvalidCode();
        }

        var consumed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE core.otp_codes
            SET is_used = true
            WHERE id = {otp.Id}
              AND tenant_id = {customer.TenantId}
              AND customer_id = {customer.Id}
              AND purpose = {purpose}
              AND NOT is_used
            """,
            cancellationToken);

        if (consumed != 1)
        {
            throw InvalidCode();
        }

        DetachOtpCodes();
        onAccepted(stamp);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Non-relational path is only for InMemory tests. The attempt update is not atomic.
    private async Task VerifyTrackedAsync(
        Customer customer,
        string purpose,
        string code,
        string secret,
        DateTimeOffset now,
        DateTimeOffset stamp,
        Action<DateTimeOffset> onAccepted,
        CancellationToken cancellationToken)
    {
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

        otp.Attempts++;
        otp.Touch();
        hmacEvaluations?.Increment();
        if (!HashMatches(secret, customer.Id, purpose, code, otp.CodeHash))
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            throw InvalidCode();
        }

        otp.MarkAsUsed();
        onAccepted(stamp);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void DetachOtpCodes()
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<OtpCode>().ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private string RequireJwtSecret() =>
        configuration["Supabase:JwtSecret"]
        ?? throw new InvalidOperationException("Supabase:JwtSecret is not configured.");

    private static bool HashMatches(
        string secret,
        Guid customerId,
        string purpose,
        string code,
        string? codeHash)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(codeHash ?? string.Empty);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = ComputeHash(secret, customerId, purpose, code);
        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

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

public sealed class OtpHmacEvaluationCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}
