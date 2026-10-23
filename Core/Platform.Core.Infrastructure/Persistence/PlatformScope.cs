using Microsoft.Extensions.Logging;

namespace Platform.Core.Infrastructure.Persistence;

/// <summary>
/// Explicit cross-tenant scope. Flows with the async context (child tasks inherit it;
/// parallel flows and callers of an awaited method do not). Nestable; dispose in LIFO order.
/// </summary>
public sealed partial class PlatformScope : IDisposable
{
    private static readonly AsyncLocal<PlatformScope?> Current = new();

    private readonly PlatformScope? _parent;
    private readonly AsyncLocal<bool> _disposed = new();

    private PlatformScope(string reason, PlatformScope? parent)
    {
        Reason = reason;
        _parent = parent;
    }

    public string Reason { get; }

    public static bool IsActive => Current.Value is not null;

    public static string? CurrentReason => Current.Value?.Reason;

    /// <param name="reason">Audit label (job or admin operation), logged on entry.</param>
    public static PlatformScope Begin(ILogger logger, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var scope = new PlatformScope(reason, Current.Value);
        LogEntered(logger, reason, scope._parent?.Reason);
        Current.Value = scope;
        return scope;
    }

    public void Dispose()
    {
        if (_disposed.Value)
        {
            return;
        }

        // Out-of-order dispose would silently drop the inner scope's restore point.
        if (!ReferenceEquals(Current.Value, this))
        {
            throw new InvalidOperationException(
                $"PlatformScope '{Reason}' disposed out of order (current: '{CurrentReason ?? "none"}').");
        }

        _disposed.Value = true;
        Current.Value = _parent;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Entering platform scope {PlatformScopeReason} (parent: {PlatformScopeParent})")]
    private static partial void LogEntered(ILogger logger, string platformScopeReason, string? platformScopeParent);
}
