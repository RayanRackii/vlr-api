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
    Task<int> ConsumeAsync(Guid tenantId, string normalizedEmail, string? clientIp, CancellationToken cancellationToken);
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
    public async Task<int> ConsumeAsync(
        Guid tenantId, string normalizedEmail, string? clientIp, CancellationToken cancellationToken)
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
            // One ordered statement consumes both buckets even when one is already blocked.
            command.CommandText = """
                INSERT INTO core.customer_login_throttle AS bucket (bucket_key, attempts, expires_at)
                SELECT bucket_key, 1, statement_timestamp() + make_interval(secs => @window)
                FROM (VALUES (@ip_key), (@email_key)) AS keys(bucket_key)
                ORDER BY bucket_key
                ON CONFLICT (bucket_key) DO UPDATE SET
                    attempts = CASE WHEN bucket.expires_at <= statement_timestamp() THEN 1
                        ELSE LEAST(bucket.attempts, CASE WHEN bucket.bucket_key = @ip_key
                            THEN @ip_limit ELSE @email_limit END) + 1 END,
                    expires_at = CASE WHEN bucket.expires_at <= statement_timestamp()
                        THEN statement_timestamp() + make_interval(secs => @window)
                        ELSE bucket.expires_at END
                RETURNING CASE WHEN attempts > CASE WHEN bucket_key = @ip_key
                    THEN @ip_limit ELSE @email_limit END
                    THEN GREATEST(1, CEIL(EXTRACT(EPOCH FROM (expires_at - statement_timestamp())))::int)
                    ELSE 0 END;
                """;
            AddParameter("ip_key", ipKey);
            AddParameter("email_key", emailKey);
            AddParameter("ip_limit", settings.IpLimit);
            AddParameter("email_limit", settings.EmailLimit);
            AddParameter("window", settings.WindowSeconds);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var retryAfter = 0;
            while (await reader.ReadAsync(cancellationToken))
                retryAfter = Math.Max(retryAfter, reader.GetInt32(0));
            return retryAfter;

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
