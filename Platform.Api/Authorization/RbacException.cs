using Platform.Core.Domain.Constants;

namespace Platform.Api.Authorization;

public sealed class RbacException : InvalidOperationException
{
    public string Code { get; }

    public int HttpStatus { get; }

    public RbacException(string code, int httpStatus)
        : base(code)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public static RbacException PrivilegeEscalation() =>
        new(RbacErrorCodes.PrivilegeEscalationBlocked, StatusCodes.Status403Forbidden);

    public static RbacException LastAdminProtected() =>
        new(RbacErrorCodes.LastAdminProtected, StatusCodes.Status409Conflict);

    public static RbacException RoleInUse() =>
        new(RbacErrorCodes.RoleInUse, StatusCodes.Status409Conflict);

    public static RbacException CannotModifySystemRole() =>
        new(RbacErrorCodes.CannotModifySystemRole, StatusCodes.Status409Conflict);

    public static RbacException CannotDeleteSystemRole() =>
        new(RbacErrorCodes.CannotDeleteSystemRole, StatusCodes.Status409Conflict);

    public static RbacException CannotAssignSuperAdmin() =>
        new(RbacErrorCodes.CannotAssignSuperAdmin, StatusCodes.Status403Forbidden);

    public static RbacException Forbidden() =>
        new(RbacErrorCodes.Forbidden, StatusCodes.Status403Forbidden);
}
