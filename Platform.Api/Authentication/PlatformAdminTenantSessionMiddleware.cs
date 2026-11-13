using System.Data.Common;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Authentication;

/// <summary>
/// Resolves the Super-Admin support tenant from live Supabase <c>app_metadata</c>
/// plus an active <c>core.users</c> membership. Exit clears that metadata while the
/// access token can still carry the previous <c>tenant_id</c>. The token's
/// <c>tenant_id</c> is only a hint for the per-process cache and is never the tenant.
/// A database failure in the membership check answers 503: withholding the tenant
/// there would turn an outage (e.g. a full Supabase pooler) into 403 on tenant routes.
/// </summary>
public sealed class PlatformAdminTenantSessionMiddleware(RequestDelegate next)
{
    public const string TenantSessionUnavailableError = "tenant_session_unavailable";

    public async Task InvokeAsync(
        HttpContext context,
        PlatformAdminTenantSession session,
        IPlatformAdminChecker platformAdminChecker,
        ISupabaseAuthAdminClient supabaseAuthAdminClient,
        AppDbContext dbContext,
        ILogger<PlatformAdminTenantSessionMiddleware> logger)
    {
        if (ShouldResolve(context, platformAdminChecker, out var authId))
        {
            // Hint only: it decides whether the per-process cache may be used.
            var hint = new TenantMetadataCacheHint(
                SupabaseAppMetadataParser.TryExtractTenantId(context.User));
            var verified = await ResolveAsync(
                session,
                supabaseAuthAdminClient,
                dbContext,
                logger,
                authId,
                hint,
                context.RequestAborted);
            if (!verified)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "1";
                await context.Response.WriteAsJsonAsync(
                    new { error = TenantSessionUnavailableError },
                    context.RequestAborted);
                return;
            }
        }

        await next(context);
    }

    private static bool ShouldResolve(
        HttpContext context,
        IPlatformAdminChecker platformAdminChecker,
        out string authId)
    {
        authId = string.Empty;
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (CustomerClaimTypes.IsCustomer(user) || !platformAdminChecker.IsPlatformAdmin(user))
        {
            return false;
        }

        var raw = user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // A blank subject is not an auth id, but the session is still resolved.
        // ResolveAsync withholds the tenant and does not call Auth admin, so a
        // stale access-token tenant_id is not selected. A present but blank
        // "sub" does not fall through to NameIdentifier.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        // Path or query characters are not sent to Auth admin. Same withhold.
        if (raw.Contains('/') || raw.Contains('?'))
        {
            return true;
        }

        authId = raw.Trim();
        return true;
    }

    /// <returns>False when the database could not be read; the request must not continue.</returns>
    private static async Task<bool> ResolveAsync(
        PlatformAdminTenantSession session,
        ISupabaseAuthAdminClient supabaseAuthAdminClient,
        AppDbContext dbContext,
        ILogger logger,
        string authId,
        TenantMetadataCacheHint hint,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authId))
        {
            session.Resolve(null);
            return true;
        }

        try
        {
            var liveTenantId = await supabaseAuthAdminClient.GetUserTenantAppMetadataAsync(
                authId,
                hint,
                cancellationToken);

            if (liveTenantId is not Guid tenantId || tenantId == Guid.Empty)
            {
                session.Resolve(null);
                return true;
            }

            // The lookup runs before the session is resolved, so the global query
            // filter would follow a stale JWT tenant or match every row. The
            // predicate pins TenantId, SupabaseAuthId, and IsActive. The tenant
            // row must still exist and be active: an active membership left behind
            // after that tenant was deleted or deactivated is not a support session.
            var isMember = await dbContext.Users
                .AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(
                    user => user.TenantId == tenantId
                        && user.SupabaseAuthId == authId
                        && user.IsActive
                        && dbContext.Tenants.Any(tenant =>
                            tenant.Id == tenantId && tenant.IsActive),
                    cancellationToken);

            session.Resolve(isMember ? tenantId : null);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DbException ex)
        {
            // The template stays free of tokens, passwords, and user payloads.
            logger.LogWarning(
                ex,
                "Platform admin tenant session could not be verified: database unavailable ({ExceptionType}). Responding 503.",
                ex.GetType().Name);
            return false;
        }
        catch (Exception ex)
        {
            var status = ex is SupabaseAuthAdminException admin ? admin.StatusCode : 0;
            // The exception object is the diagnostic. The template stays free of
            // tokens, passwords, and user payloads.
            logger.LogWarning(
                ex,
                "Platform admin tenant session could not be verified ({ExceptionType}, status {StatusCode}). Tenant context withheld.",
                ex.GetType().Name,
                status);
            session.Resolve(null);
            return true;
        }
    }
}
