using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class Role : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    public bool IsSystemRole { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    private readonly List<UserRole> _userRoles = [];

    private readonly List<RolePermission> _rolePermissions = [];

    public IReadOnlyCollection<UserRole> UserRoles => _userRoles.AsReadOnly();

    public IReadOnlyCollection<RolePermission> RolePermissions => _rolePermissions.AsReadOnly();

    private Role()
    {
    }

    public Role(Guid tenantId, string name, string? description = null, bool isSystemRole = false)
    {
        TenantId = tenantId;
        Name = name;
        Description = description;
        IsSystemRole = isSystemRole;
    }

    public void UpdateProfile(string name, string? description)
    {
        Name = name;
        Description = description;
        MarkAsUpdated();
    }
}
