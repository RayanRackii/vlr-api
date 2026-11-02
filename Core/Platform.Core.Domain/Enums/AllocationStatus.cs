namespace Platform.Core.Domain.Enums;

/// <summary>
/// Lifecycle of a <see cref="Entities.CatalogOrderItemResourceAllocation"/>.
/// Scheduled -&gt; Running -&gt; Cleaning -&gt; Released. An unstarted allocation can become Cancelled.
/// </summary>
public enum AllocationStatus
{
    Scheduled = 0,
    Running = 1,
    Cleaning = 2,
    Released = 3,
    Cancelled = 4,
}
