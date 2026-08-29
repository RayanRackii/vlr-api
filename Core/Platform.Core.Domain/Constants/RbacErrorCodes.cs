namespace Platform.Core.Domain.Constants;

public static class RbacErrorCodes
{
    public const string PrivilegeEscalationBlocked = "PRIVILEGE_ESCALATION_BLOCKED";
    public const string LastAdminProtected = "LAST_ADMIN_PROTECTED";
    public const string RoleInUse = "ROLE_IN_USE";
    public const string CannotModifySystemRole = "CANNOT_MODIFY_SYSTEM_ROLE";
    public const string CannotDeleteSystemRole = "CANNOT_DELETE_SYSTEM_ROLE";
    public const string CannotAssignSuperAdmin = "CANNOT_ASSIGN_SUPERADMIN";
    public const string Forbidden = "FORBIDDEN";
}
