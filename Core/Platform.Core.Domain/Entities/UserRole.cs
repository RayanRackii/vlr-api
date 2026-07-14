namespace Platform.Core.Domain.Entities;

public class UserRole
{
    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    public User User { get; private set; } = null!;

    public Role Role { get; private set; } = null!;

    private UserRole()
    {
    }

    public UserRole(Guid userId, Guid roleId)
    {
        UserId = userId;
        RoleId = roleId;
        AssignedAt = DateTimeOffset.UtcNow;
    }
}
