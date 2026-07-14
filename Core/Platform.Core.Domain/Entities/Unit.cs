using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class Unit : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = null!;

    public string? Code { get; private set; }

    public bool IsActive { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    private Unit()
    {
    }

    public Unit(Guid tenantId, string name, string? code = null)
    {
        TenantId = tenantId;
        Name = name;
        Code = code;
        IsActive = true;
    }

    public void UpdateProfile(string name, string? code)
    {
        Name = name;
        Code = code;
        MarkAsUpdated();
    }

    public void Activate()
    {
        IsActive = true;
        MarkAsUpdated();
    }

    public void Deactivate()
    {
        IsActive = false;
        MarkAsUpdated();
    }
}
