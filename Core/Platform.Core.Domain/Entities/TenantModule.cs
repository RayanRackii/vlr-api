using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Links a Tenant to a subscribed platform module (e.g. inventory, pmoc, os, rentals).
/// </summary>
public class TenantModule : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public string ModuleName { get; private set; } = null!;

    public bool IsActive { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    private TenantModule()
    {
    }

    public TenantModule(Guid tenantId, string moduleName, bool isActive = true)
    {
        TenantId = tenantId;
        ModuleName = moduleName;
        IsActive = isActive;
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
