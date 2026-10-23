using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.CustomerAuth.Services;

public interface ICustomerLoginThrottle
{
    Task<int> CheckAsync(Guid tenantId, string normalizedEmail, string? clientIp, CancellationToken cancellationToken);
    Task<int> ConsumeAsync(Guid tenantId, string normalizedEmail, string? clientIp, bool failed, CancellationToken cancellationToken);
}

public sealed class CustomerLoginThrottleOptions
{
    public const string SectionName = "CustomerLoginThrottle";
    public int EmailLimit { get; set; } = 5;
    public int IpLimit { get; set; } = 30;
    public int WindowSeconds { get; set; } = 900;
}

public sealed class CustomerLoginRateLimitedException(int retryAfterSeconds)
    : Exception("Too many login attempts. Try again later.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class CustomerLoginThrottle(
    AppDbContext dbContext,
    IConfiguration configuration,
    IOptions<CustomerLoginThrottleOptions> options) : ICustomerLoginThrottle
{
    public Task<int> CheckAsync(
        Guid tenantId, string normalizedEmail, string? clientIp, CancellationToken cancellationToken) =>
        ExecuteAsync(tenantId, normalizedEmail, clientIp, null, cancellationToken);

    public Task<int> ConsumeAsync(
        Guid tenantId, string normalizedEmail, string? clientIp, bool failed, CancellationToken cancellationToken) =>
        ExecuteAsync(tenantId, normalizedEmail, clientIp, failed, cancellationToken);

    private async Task<int> ExecuteAsync(
        Guid tenantId, string normalizedEmail, string? clientIp, bool? failed, CancellationToken cancellationToken)
    {
        var secret = configuration["Supabase:JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Supabase:JwtSecret is required for customer login throttling.");

        var settings = options.Value;
        var ipKey = "0:" + Hash(secret, "customer-login:ip:" + (clientIp ?? "unknown"));
        var emailKey = "1:" + Hash(secret,
            $"customer-login:email:{tenantId:D}:{normalizedEmail.Trim().ToLowerInvariant()}");
        var connection = dbContext.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
            // Resolve the global IP budget first; blocked IPs cannot spend an email budget.
            command.CommandText = failed.HasValue ? """
                WITH ip_consumed AS (
                    INSERT INTO core.customer_login_throttle AS bucket (bucket_key, attempts, expires_at)
                    SELECT @ip_key, 1, statement_timestamp() + make_interval(secs => @window)
                    WHERE @failed
                    ON CONFLICT (bucket_key) DO UPDATE SET
                        attempts = CASE WHEN bucket.expires_at <= statement_timestamp() THEN 1
                            ELSE LEAST(bucket.attempts, @ip_limit) + 1 END,
                        expires_at = CASE WHEN bucket.expires_at <= statement_timestamp()
                            THEN statement_timestamp() + make_interval(secs => @window)
                            ELSE bucket.expires_at END
                    RETURNING CASE WHEN attempts > @ip_limit
                        THEN GREATEST(1, CEIL(EXTRACT(EPOCH FROM (expires_at - statement_timestamp())))::int)
                        ELSE 0 END AS retry_after
                ), ip_gate AS (
                    SELECT COALESCE(MAX(retry_after), 0) AS retry_after FROM (
                        SELECT retry_after FROM ip_consumed
                        UNION ALL
                        SELECT GREATEST(1, CEIL(EXTRACT(EPOCH FROM (expires_at - statement_timestamp())))::int)
                        FROM core.customer_login_throttle
                        WHERE NOT @failed AND bucket_key = @ip_key
                            AND attempts >= @ip_limit AND expires_at > statement_timestamp()
                    ) AS decisions
                ), email_consumed AS (
                    INSERT INTO core.customer_login_throttle AS bucket (bucket_key, attempts, expires_at)
                    SELECT @email_key, 1, statement_timestamp() + make_interval(secs => @window)
                    FROM ip_gate WHERE retry_after = 0 AND @failed
                    ON CONFLICT (bucket_key) DO UPDATE SET
                        attempts = CASE WHEN bucket.expires_at <= statement_timestamp() THEN 1
                            ELSE LEAST(bucket.attempts, @email_limit) + 1 END,
                        expires_at = CASE WHEN bucket.expires_at <= statement_timestamp()
                            THEN statement_timestamp() + make_interval(secs => @window)
                            ELSE bucket.expires_at END
                    RETURNING CASE WHEN attempts > @email_limit
                        THEN GREATEST(1, CEIL(EXTRACT(EPOCH FROM (expires_at - statement_timestamp())))::int)
                        ELSE 0 END AS retry_after
                )
                SELECT retry_after FROM ip_gate
                UNION ALL SELECT retry_after FROM email_consumed;
                """ : """
                SELECT GREATEST(1, CEIL(EXTRACT(EPOCH FROM (expires_at - statement_timestamp())))::int)
                FROM core.customer_login_throttle
                WHERE expires_at > statement_timestamp()
                    AND ((bucket_key = @ip_key AND attempts >= @ip_limit)
                        OR (bucket_key = @email_key AND attempts >= @email_limit));
                """;
            AddParameter("ip_key", ipKey);
            AddParameter("email_key", emailKey);
            AddParameter("ip_limit", settings.IpLimit);
            AddParameter("email_limit", settings.EmailLimit);
            AddParameter("window", settings.WindowSeconds);
            AddParameter("failed", failed ?? false);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var retryAfter = 0;
            while (await reader.ReadAsync(cancellationToken))
                retryAfter = Math.Max(retryAfter, reader.GetInt32(0));
            return Math.Min(retryAfter, settings.WindowSeconds);

            void AddParameter(string name, object value)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
        }
        finally
        {
            if (opened)
                await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static string Hash(string secret, string value) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(value)));
}
