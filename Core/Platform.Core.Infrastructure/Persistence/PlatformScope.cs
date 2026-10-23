using Microsoft.Extensions.Logging;

namespace Platform.Core.Infrastructure.Persistence;

/// <summary>
/// Explicit cross-tenant scope. Flows with the async context (child tasks inherit it;
/// parallel flows and callers of an awaited method do not). Nestable; dispose in LIFO order.
/// </summary>
public sealed partial class PlatformScope : IDisposable
{
    private static readonly AsyncLocal<ScopeFrame?> Current = new();

    private readonly ScopeFrame _frame;

    private PlatformScope(string reason, ScopeFrame? previous)
    {
        Reason = reason;
        _frame = new ScopeFrame(TenantContextMode.Platform, null, previous, this);
    }

    public string Reason { get; }

    public static bool IsActive => Current.Value?.Mode == TenantContextMode.Platform;

    public static string? CurrentReason => Current.Value?.Owner.Reason;

    /// <param name="reason">Audit label (job or admin operation), logged on entry.</param>
    public static PlatformScope Begin(ILogger logger, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var scope = new PlatformScope(reason, Current.Value);
        LogEntered(logger, reason, scope._frame.Previous?.Owner.Reason);
        Current.Value = scope._frame;
        return scope;
    }

    public void Dispose()
    {
        var current = Current.Value;
        if (ReferenceEquals(current?.Owner, this))
        {
            Current.Value = current.Previous;
            return;
        }

        // Out-of-order dispose would silently drop the inner scope's restore point.
        if (Contains(current, this))
        {
            throw new InvalidOperationException(
                $"PlatformScope '{Reason}' disposed out of order (current: '{CurrentReason ?? "none"}').");
        }
    }

    private static bool Contains(ScopeFrame? frame, PlatformScope scope)
    {
        for (var current = frame; current is not null; current = current.Previous)
        {
            if (ReferenceEquals(current.Owner, scope)) return true;
        }

        return false;
    }

    private sealed record ScopeFrame(
        TenantContextMode Mode,
        Guid? TenantId,
        ScopeFrame? Previous,
        PlatformScope Owner);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Entering platform scope {PlatformScopeReason} (parent: {PlatformScopeParent})")]
    private static partial void LogEntered(ILogger logger, string platformScopeReason, string? platformScopeParent);
}
