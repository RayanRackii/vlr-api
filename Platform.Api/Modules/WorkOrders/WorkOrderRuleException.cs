namespace Platform.Api.Modules.WorkOrders;

public static class WorkOrderErrorCodes
{
    public const string Closed = "WORK_ORDER_CLOSED";

    public const string ReopenNotAllowed = "WORK_ORDER_REOPEN_NOT_ALLOWED";

    public const string InvalidTaskValue = "INVALID_TASK_VALUE";

    public const string MandatoryTasksIncomplete = "MANDATORY_TASKS_INCOMPLETE";

    public const string AssigneeCannotExecute = "ASSIGNEE_CANNOT_EXECUTE";
}

public sealed record IncompleteWorkOrderTask(Guid Id, string Title);

public abstract class WorkOrderRuleException : Exception
{
    protected WorkOrderRuleException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class WorkOrderClosedException()
    : WorkOrderRuleException(
        WorkOrderErrorCodes.Closed,
        "Work order is completed or canceled and cannot be changed.")
{
}

public sealed class WorkOrderReopenNotAllowedException()
    : WorkOrderRuleException(
        WorkOrderErrorCodes.ReopenNotAllowed,
        "A completed work order cannot be reopened.")
{
}

public sealed class InvalidTaskValueException : WorkOrderRuleException
{
    public const string NotANumber = "NOT_A_NUMBER";

    public const string OutOfRange = "OUT_OF_RANGE";

    public const string NotAnOption = "NOT_AN_OPTION";

    public const string InvalidCheckbox = "INVALID_CHECKBOX";

    public const string TextTooLong = "TEXT_TOO_LONG";

    public const string InvalidConfiguration = "INVALID_CONFIGURATION";

    public InvalidTaskValueException(string reason, string message)
        : base(WorkOrderErrorCodes.InvalidTaskValue, message)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

public sealed class MandatoryTasksIncompleteException : WorkOrderRuleException
{
    public MandatoryTasksIncompleteException(IReadOnlyList<IncompleteWorkOrderTask> tasks)
        : base(
            WorkOrderErrorCodes.MandatoryTasksIncomplete,
            "Mandatory tasks are incomplete.")
    {
        Tasks = tasks;
    }

    public IReadOnlyList<IncompleteWorkOrderTask> Tasks { get; }
}

public sealed class AssigneeCannotExecuteException : ArgumentException
{
    public const string Code = WorkOrderErrorCodes.AssigneeCannotExecute;

    public AssigneeCannotExecuteException(Guid userId)
        : base($"Assigned user '{userId}' cannot execute work orders.")
    {
        UserId = userId;
    }

    public Guid UserId { get; }
}
