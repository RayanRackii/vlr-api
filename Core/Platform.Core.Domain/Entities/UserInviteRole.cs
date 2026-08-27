namespace Platform.Core.Domain.Entities;

/// <summary>
/// Join table: pending invite → tenant role. Not tenant-scoped itself.
/// </summary>
public class UserInviteRole
{
    public Guid InviteId { get; private set; }

    public Guid RoleId { get; private set; }

    public UserInvite Invite { get; private set; } = null!;

    public Role Role { get; private set; } = null!;

    private UserInviteRole()
    {
    }

    public UserInviteRole(Guid inviteId, Guid roleId)
    {
        InviteId = inviteId;
        RoleId = roleId;
    }
}
