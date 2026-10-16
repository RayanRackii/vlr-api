using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class User : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public string SupabaseAuthId { get; private set; } = null!;

    public string FullName { get; private set; } = null!;

    public string Email { get; private set; } = null!;

    /// <summary>
    /// Staff contact number stored as E.164, or null when the person has not set one.
    /// This is not a customer document and is not an identity key.
    /// </summary>
    public string? Phone { get; private set; }

    public bool IsActive { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    private readonly List<UserRole> _userRoles = [];

    public IReadOnlyCollection<UserRole> UserRoles => _userRoles.AsReadOnly();

    private User()
    {
    }

    public User(Guid tenantId, string supabaseAuthId, string fullName, string email)
    {
        TenantId = tenantId;
        SupabaseAuthId = supabaseAuthId;
        FullName = fullName;
        Email = email;
        IsActive = true;
    }

    public void UpdateProfile(string fullName, string email)
    {
        FullName = fullName;
        Email = email;
        MarkAsUpdated();
    }

    public void SetPhone(string? phone)
    {
        Phone = phone;
        MarkAsUpdated();
    }

    public void LinkSupabaseAuthId(string supabaseAuthId)
    {
        if (string.IsNullOrWhiteSpace(supabaseAuthId))
        {
            throw new ArgumentException("SupabaseAuthId is required.", nameof(supabaseAuthId));
        }

        SupabaseAuthId = supabaseAuthId.Trim();
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
