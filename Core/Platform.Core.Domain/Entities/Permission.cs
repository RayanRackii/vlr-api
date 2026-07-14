using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class Permission : Entity
{
    public string Key { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    public string? ModuleKey { get; private set; }

    private readonly List<RolePermission> _rolePermissions = [];

    public IReadOnlyCollection<RolePermission> RolePermissions => _rolePermissions.AsReadOnly();

    private Permission()
    {
    }

    public Permission(string key, string name, string? description = null, string? moduleKey = null)
    {
        Key = key;
        Name = name;
        Description = description;
        ModuleKey = moduleKey;
    }
}
