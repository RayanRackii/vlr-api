namespace Platform.Api.Modules.WorkOrders;

public sealed class DuplicateWorkOrderException()
    : InvalidOperationException(
        "A work order already exists for this maintenance plan, asset, and scheduled date.")
{
    public const string Code = "DUPLICATE_WORK_ORDER";
}
