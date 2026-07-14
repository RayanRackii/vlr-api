using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class Tenant : Entity
{
    public string LegalName { get; private set; } = null!;

    public string? TradeName { get; private set; }

    public string TaxId { get; private set; } = null!;

    public bool IsActive { get; private set; }

    private readonly List<User> _users = [];

    private readonly List<Unit> _units = [];

    private readonly List<Role> _roles = [];

    public IReadOnlyCollection<User> Users => _users.AsReadOnly();

    public IReadOnlyCollection<Unit> Units => _units.AsReadOnly();

    public IReadOnlyCollection<Role> Roles => _roles.AsReadOnly();

    private Tenant()
    {
    }

    public Tenant(string legalName, string taxId, string? tradeName = null)
    {
        LegalName = legalName;
        TaxId = taxId;
        TradeName = tradeName;
        IsActive = true;
    }

    public void UpdateProfile(string legalName, string taxId, string? tradeName)
    {
        LegalName = legalName;
        TaxId = taxId;
        TradeName = tradeName;
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
