using Platform.Core.Domain.Events;

namespace Platform.Api.DomainEvents;

public interface IDomainEventDispatcher<in TEvent> where TEvent : IDomainEvent
{
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> DispatchAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatches sequentially: handlers share the caller's scoped DbContext and transaction.
/// Persistence and after-commit scheduling remain the caller's responsibility.
/// </summary>
public sealed class DomainEventDispatcher<TEvent>(IEnumerable<IDomainEventHandler<TEvent>> handlers)
    : IDomainEventDispatcher<TEvent> where TEvent : IDomainEvent
{
    public async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        foreach (var handler in handlers)
            await handler.EnsureReadyAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> DispatchAsync(
        TEvent domainEvent, CancellationToken cancellationToken = default)
    {
        var deliveries = new List<Guid>();
        foreach (var handler in handlers)
            deliveries.AddRange(await handler.HandleAsync(domainEvent, cancellationToken));
        return deliveries;
    }
}
