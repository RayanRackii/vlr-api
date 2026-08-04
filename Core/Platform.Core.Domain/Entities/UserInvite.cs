using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Pending B2B user invite. Recipient sets their own password via /invite?token=.
/// </summary>
public class UserInvite : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = null!;

    public string FullName { get; private set; } = null!;

    /// <summary>System role name to assign on accept (e.g. Admin).</summary>
    public string RoleName { get; private set; } = null!;

    public string Token { get; private set; } = null!;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public Guid? CreatedUserId { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    private UserInvite()
    {
    }

    public UserInvite(
        Guid tenantId,
        string email,
        string fullName,
        string roleName,
        string token,
        DateTimeOffset expiresAt)
    {
        TenantId = tenantId;
        Email = email.Trim().ToLowerInvariant();
        FullName = fullName.Trim();
        RoleName = roleName.Trim();
        Token = token;
        ExpiresAt = expiresAt;
    }

    public bool IsPending =>
        AcceptedAt is null
        && RevokedAt is null
        && ExpiresAt > DateTimeOffset.UtcNow;

    public void MarkAccepted(Guid userId)
    {
        AcceptedAt = DateTimeOffset.UtcNow;
        CreatedUserId = userId;
        MarkAsUpdated();
    }

    public void Revoke()
    {
        RevokedAt = DateTimeOffset.UtcNow;
        MarkAsUpdated();
    }
}
