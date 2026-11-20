using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Common;

/// <summary>Contract of the optional <c>Idempotency-Key</c> request header.</summary>
public static class IdempotencyHeader
{
    public const string Name = "Idempotency-Key";

    public const int MaxLength = IdempotencyKey.KeyMaxLength;

    /// <summary>A key is 1 to 100 characters, not blank, with no control characters.</summary>
    public static bool IsValid(string key) =>
        key.Length is >= 1 and <= MaxLength
        && !string.IsNullOrWhiteSpace(key)
        && !key.Any(char.IsControl);
}

/// <summary>Stable error codes of the idempotency contract (HTTP 409 and 400 bodies).</summary>
public static class IdempotencyErrorCodes
{
    public const string KeyReused = "idempotency.key_reused";
    public const string InProgress = "idempotency.in_progress";
    public const string KeyInvalid = "idempotency.key_invalid";
    public const string ResourceMissing = "idempotency.resource_missing";
}

public enum IdempotencyOutcome
{
    /// <summary>The key was claimed: run the operation, then complete or abandon the claim.</summary>
    Proceed,

    /// <summary>The key was already used with the same payload: return the stored resource.</summary>
    Replay,

    /// <summary>The key was already used with a different payload.</summary>
    KeyReused,

    /// <summary>The first request with this key is still running after the wait budget.</summary>
    InProgress,
}

public sealed record IdempotencyBegin(
    IdempotencyOutcome Outcome,
    Guid ClaimId = default,
    Guid? ResourceId = null,
    int? ResponseStatus = null);

public sealed class IdempotencyOptions
{
    /// <summary>How long a request waits for the first request with the same key to finish.</summary>
    public TimeSpan PendingWait { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A claim that never completed (the process died) is taken over after this long, so a crash
    /// cannot block the key for its whole retention.
    /// </summary>
    public TimeSpan PendingStaleAfter { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Stores <c>Idempotency-Key</c> claims per (tenant, scope, customer, key). The caller claims the key
/// with <see cref="BeginAsync"/>, runs the operation only on <see cref="IdempotencyOutcome.Proceed"/>
/// and then calls <see cref="CompleteAsync"/> with the created resource id, or
/// <see cref="AbandonAsync"/> when the operation failed (so a retry can run it again).
/// </summary>
public interface IIdempotencyService
{
    Task<IdempotencyBegin> BeginAsync(
        string scope,
        Guid principalId,
        string key,
        object payload,
        CancellationToken cancellationToken);

    Task CompleteAsync(Guid claimId, Guid resourceId, int responseStatus, CancellationToken cancellationToken);

    Task AbandonAsync(Guid claimId, CancellationToken cancellationToken);
}

public sealed class IdempotencyService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    TimeProvider? timeProvider = null,
    IdempotencyOptions? options = null) : IIdempotencyService
{
    private static readonly JsonSerializerOptions HashJson = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly IdempotencyOptions settings = options ?? new IdempotencyOptions();

    public async Task<IdempotencyBegin> BeginAsync(
        string scope,
        Guid principalId,
        string key,
        object payload,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new InvalidOperationException("Idempotency requires a tenant context.");
        var requestHash = HashPayload(payload);
        var deadline = clock.GetUtcNow() + settings.PendingWait;

        while (true)
        {
            var now = clock.GetUtcNow();
            var row = await dbContext.IdempotencyKeys
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    k => k.TenantId == tenantId
                        && k.Scope == scope
                        && k.PrincipalId == principalId
                        && k.Key == key,
                    cancellationToken);

            if (row is null)
            {
                var claim = new IdempotencyKey(tenantId, scope, principalId, key, requestHash, now);
                dbContext.IdempotencyKeys.Add(claim);
                try
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                    return new IdempotencyBegin(IdempotencyOutcome.Proceed, claim.Id);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // A concurrent request claimed the key first: read its row on the next pass.
                    dbContext.Entry(claim).State = EntityState.Detached;
                }
            }
            else if (row.ExpiresAt <= now)
            {
                await DeleteAsync(row.Id, cancellationToken);
            }
            else if (!string.Equals(row.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return new IdempotencyBegin(IdempotencyOutcome.KeyReused);
            }
            else if (row.ResourceId is { } resourceId)
            {
                return new IdempotencyBegin(
                    IdempotencyOutcome.Replay,
                    row.Id,
                    resourceId,
                    row.ResponseStatus);
            }
            else if (now - row.CreatedAt >= settings.PendingStaleAfter)
            {
                // The first request never finished (crash): free the key and claim it again.
                await DeleteAsync(row.Id, cancellationToken);
            }
            else
            {
                if (now >= deadline)
                {
                    return new IdempotencyBegin(IdempotencyOutcome.InProgress);
                }

                await Task.Delay(settings.PollInterval, clock, cancellationToken);
                continue;
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return new IdempotencyBegin(IdempotencyOutcome.InProgress);
            }
        }
    }

    public async Task CompleteAsync(
        Guid claimId,
        Guid resourceId,
        int responseStatus,
        CancellationToken cancellationToken)
    {
        var claim = await dbContext.IdempotencyKeys
            .FirstOrDefaultAsync(k => k.Id == claimId, cancellationToken);
        if (claim is null)
        {
            // A stale-claim takeover removed the row while this slow request was still running.
            // The resource exists, so the response stays a success; the key just is not stored.
            return;
        }

        claim.Complete(resourceId, responseStatus);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task AbandonAsync(Guid claimId, CancellationToken cancellationToken)
    {
        // The operation failed part-way: drop whatever it left in the change tracker so the delete
        // below cannot try to persist it. The request ends with an error response right after.
        dbContext.ChangeTracker.Clear();
        await DeleteAsync(claimId, cancellationToken);
    }

    private async Task DeleteAsync(Guid claimId, CancellationToken cancellationToken)
    {
        var row = await dbContext.IdempotencyKeys
            .FirstOrDefaultAsync(k => k.Id == claimId, cancellationToken);
        if (row is null)
        {
            return;
        }

        dbContext.IdempotencyKeys.Remove(row);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request deleted the same row first; the goal (the row is gone) is met.
            dbContext.Entry(row).State = EntityState.Detached;
        }
    }

    private static string HashPayload(object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), HashJson);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation;
}
