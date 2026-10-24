using Platform.Core.Domain.Events;

namespace Platform.Api.DomainEvents;

public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    // Seed notification templates/configs before opening the business transaction.
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages effects in the caller's scoped DbContext without saving or sending.
    /// Returns external delivery IDs to schedule only after the caller commits.
    /// </summary>
    Task<IReadOnlyList<Guid>> HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);
}
