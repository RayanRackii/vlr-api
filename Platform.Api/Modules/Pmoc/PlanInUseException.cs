namespace Platform.Api.Modules.Pmoc;

public sealed class PlanInUseException()
    : InvalidOperationException(
        "This maintenance plan is in use by one or more work orders. Set IsActive=false to deactivate it instead of deleting.")
{
    public const string Code = "PLAN_IN_USE";
}
