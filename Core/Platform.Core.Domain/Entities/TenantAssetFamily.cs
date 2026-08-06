using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Tenant opt-in to a platform <see cref="AssetFamily"/>.
/// </summary>
public class TenantAssetFamily : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public Guid FamilyId { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    public AssetFamily Family { get; private set; } = null!;

    private TenantAssetFamily()
    {
    }

    public TenantAssetFamily(Guid tenantId, Guid familyId)
    {
        TenantId = tenantId;
        FamilyId = familyId;
    }
}
