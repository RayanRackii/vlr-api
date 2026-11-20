using Microsoft.AspNetCore.Mvc;

namespace Platform.Api.Common;

/// <summary>
/// Shared controller flow of an idempotent create: claim the key, run the create once, store the
/// created resource id, and answer a replay with the stored resource and status.
/// </summary>
public static class IdempotentCreateExtensions
{
    /// <summary>
    /// Returns the 400 for a header that is present but not a valid key; null when the key is fine.
    /// </summary>
    public static ActionResult? ValidateIdempotencyKey(this ControllerBase controller, string key) =>
        IdempotencyHeader.IsValid(key)
            ? null
            : controller.BadRequest(new
            {
                error = $"{IdempotencyHeader.Name} must be 1 to {IdempotencyHeader.MaxLength} printable characters.",
                code = IdempotencyErrorCodes.KeyInvalid,
            });

    /// <summary>
    /// Runs <paramref name="create"/> at most once per (tenant, scope, customer, key). Exceptions of
    /// <paramref name="create"/> propagate to the caller's own mapping after the claim is released,
    /// so a retry after a failed attempt runs the operation again.
    /// </summary>
    public static async Task<ActionResult<T>> CreateIdempotentAsync<T>(
        this ControllerBase controller,
        IIdempotencyService? idempotency,
        string scope,
        Guid principalId,
        string key,
        object payload,
        int successStatus,
        Func<Task<T>> create,
        Func<T, Guid> resourceId,
        Func<Guid, Task<T?>> loadExisting,
        Func<T, string>? location,
        CancellationToken cancellationToken)
        where T : class
    {
        if (idempotency is null)
        {
            throw new InvalidOperationException("Idempotency is not configured for this host.");
        }

        var begin = await idempotency.BeginAsync(scope, principalId, key, payload, cancellationToken);
        switch (begin.Outcome)
        {
            case IdempotencyOutcome.KeyReused:
                return controller.Conflict(new
                {
                    error = $"The {IdempotencyHeader.Name} was already used with a different request.",
                    code = IdempotencyErrorCodes.KeyReused,
                });

            case IdempotencyOutcome.InProgress:
                controller.Response.Headers.RetryAfter = "1";
                return controller.Conflict(new
                {
                    error = "A request with this Idempotency-Key is still being processed.",
                    code = IdempotencyErrorCodes.InProgress,
                });

            case IdempotencyOutcome.Replay:
                var existing = await loadExisting(begin.ResourceId!.Value);
                return existing is null
                    ? controller.NotFound(new
                    {
                        error = "The resource created by the original request no longer exists.",
                        code = IdempotencyErrorCodes.ResourceMissing,
                    })
                    : Respond(controller, existing, begin.ResponseStatus ?? successStatus, location);
        }

        T created;
        try
        {
            created = await create();
        }
        catch
        {
            await idempotency.AbandonAsync(begin.ClaimId, CancellationToken.None);
            throw;
        }

        // The resource exists from here on, so the claim is completed even if the client went away.
        await idempotency.CompleteAsync(begin.ClaimId, resourceId(created), successStatus, CancellationToken.None);
        return Respond(controller, created, successStatus, location);
    }

    private static ActionResult<T> Respond<T>(
        ControllerBase controller,
        T body,
        int status,
        Func<T, string>? location)
        where T : class
    {
        if (status == StatusCodes.Status201Created && location is not null)
        {
            return controller.Created(location(body), body);
        }

        if (status == StatusCodes.Status200OK)
        {
            return controller.Ok(body);
        }

        return controller.StatusCode(status, body);
    }
}
