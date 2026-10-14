using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Authentication;

/// <summary>
/// Resolves the Super-Admin support tenant from live Supabase <c>app_metadata</c>
/// plus an active <c>core.users</c> membership. Exit clears that metadata while the
/// access token can still carry the previous <c>tenant_id</c>; this middleware
/// does not read that claim.
/// </summary>
public sealed class PlatformAdminTenantSessionMiddleware(RequestDelegate next)
{
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
            await ResolveAsync(
                session,
                supabaseAuthAdminClient,
                dbContext,
                logger,
                authId,
                context.RequestAborted);
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

    private static async Task ResolveAsync(
        PlatformAdminTenantSession session,
        ISupabaseAuthAdminClient supabaseAuthAdminClient,
        AppDbContext dbContext,
        ILogger logger,
        string authId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authId))
        {
            session.Resolve(null);
            return;
        }

        try
        {
            var liveTenantId = await supabaseAuthAdminClient.GetUserTenantAppMetadataAsync(
                authId,
                cancellationToken);

            if (liveTenantId is not Guid tenantId || tenantId == Guid.Empty)
            {
                session.Resolve(null);
                return;
            }

            // The lookup runs before the session is resolved, so the global query
            // filter would follow a stale JWT tenant or match every row. The
            // predicate pins TenantId, SupabaseAuthId, and IsActive.
            var isMember = await dbContext.Users
                .AsNoTracking()
                .IgnoreQueryFilters()
                .AnyAsync(
                    user => user.TenantId == tenantId
                        && user.SupabaseAuthId == authId
                        && user.IsActive,
                    cancellationToken);

            session.Resolve(isMember ? tenantId : null);
        }
        catch (OperationCanceledException)
        {
            throw;
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
        }
    }
}
