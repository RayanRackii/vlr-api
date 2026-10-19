using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Modules.Users.Dtos;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Modules.Users.Services;

public interface IStaffTenantMembershipService
{
    Task<StaffTenantMembershipListResponse> ListAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken);

    Task<SelectStaffTenantResponse> SelectAsync(
        ClaimsPrincipal principal,
        Guid tenantId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Staff login is a global Supabase email/password. The access token carries one
/// <c>app_metadata.tenant_id</c>. This service lists the caller's active memberships
/// and writes that metadata when they pick one. It does not change passwords.
/// </summary>
public sealed class StaffTenantMembershipService(
    AppDbContext dbContext,
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    IPlatformAdminChecker platformAdminChecker) : IStaffTenantMembershipService
{
    public async Task<StaffTenantMembershipListResponse> ListAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var (email, authId) = RequireStaffIdentity(principal);
        var memberships = await LoadMembershipsAsync(email, authId, tenantId: null, cancellationToken);
        var metadataTenantId = await supabaseAuthAdminClient.GetUserTenantAppMetadataAsync(
            memberships.FirstOrDefault()?.AuthId ?? authId,
            cancellationToken);
        var accessTokenTenantId = SupabaseAppMetadataParser.TryExtractTenantId(principal);

        return new StaffTenantMembershipListResponse(
            accessTokenTenantId,
            metadataTenantId,
            SessionRefreshRequired: accessTokenTenantId != metadataTenantId,
            memberships
                .Select(item => new StaffTenantMembershipDto(
                    item.TenantId,
                    item.LegalName,
                    item.TradeName,
                    item.Subdomain))
                .ToList());
    }

    public async Task<SelectStaffTenantResponse> SelectAsync(
        ClaimsPrincipal principal,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId is required.");
        }

        var (email, authId) = RequireStaffIdentity(principal);
        var membership = await LoadMembershipsAsync(email, authId, tenantId, cancellationToken);
        var selected = membership.SingleOrDefault()
            ?? throw new KeyNotFoundException("Tenant membership was not found.");

        await supabaseAuthAdminClient.UpdateUserAppMetadataAsync(
            selected.AuthId,
            selected.TenantId,
            cancellationToken);

        var accessTokenTenantId = SupabaseAppMetadataParser.TryExtractTenantId(principal);
        return new SelectStaffTenantResponse(
            selected.TenantId,
            selected.LegalName,
            selected.TradeName,
            selected.Subdomain,
            SessionRefreshRequired: accessTokenTenantId != selected.TenantId);
    }

    private (string Email, string AuthId) RequireStaffIdentity(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || CustomerClaimTypes.IsCustomer(principal))
        {
            throw new UnauthorizedAccessException("Staff authentication is required.");
        }

        if (platformAdminChecker.IsPlatformAdmin(principal))
        {
            throw new InvalidOperationException("PLATFORM_ADMIN_USES_ENTER");
        }

        var email = principal.FindFirst("email")?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value;
        var authId = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new UnauthorizedAccessException("Authenticated email is required.");
        }

        if (string.IsNullOrWhiteSpace(authId))
        {
            throw new UnauthorizedAccessException("The authenticated user identifier is missing.");
        }

        return (email.Trim().ToLowerInvariant(), authId.Trim());
    }

    private async Task<List<MembershipRow>> LoadMembershipsAsync(
        string email,
        string authId,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var normalizedAuthId = authId.ToLowerInvariant();
        var query = dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(user => user.IsActive
                && user.Email.ToLower() == email
                && user.SupabaseAuthId.ToLower() == normalizedAuthId);

        if (tenantId is Guid selectedTenantId)
        {
            query = query.Where(user => user.TenantId == selectedTenantId);
        }

        var rows = await query
            .Join(
                dbContext.Tenants.AsNoTracking().Where(tenant => tenant.IsActive),
                user => user.TenantId,
                tenant => tenant.Id,
                (user, tenant) => new MembershipRow(
                    user.SupabaseAuthId,
                    tenant.Id,
                    tenant.LegalName,
                    tenant.TradeName,
                    tenant.Subdomain))
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(row => row.LegalName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.TenantId)
            .ToList();
    }

    private sealed record MembershipRow(
        string AuthId,
        Guid TenantId,
        string LegalName,
        string? TradeName,
        string? Subdomain);
}
