namespace Platform.Core.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }

    public DateTimeOffset? UpdatedAt { get; protected set; }

    protected Entity()
    {
        Id = Guid.NewGuid();
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public void Touch()
    {
        MarkAsUpdated();
    }

    protected void MarkAsUpdated()
    {
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
