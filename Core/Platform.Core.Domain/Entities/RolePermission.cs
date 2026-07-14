namespace Platform.Core.Domain.Entities;

public class RolePermission
{
    public Guid RoleId { get; private set; }

    public Guid PermissionId { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public Role Role { get; private set; } = null!;

    public Permission Permission { get; private set; } = null!;

    private RolePermission()
    {
    }

    public RolePermission(Guid roleId, Guid permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
        GrantedAt = DateTimeOffset.UtcNow;
    }
}
