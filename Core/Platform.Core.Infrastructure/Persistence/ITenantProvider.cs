namespace Platform.Core.Infrastructure.Persistence;

public interface ITenantProvider
{
    Guid? TenantId { get; }
}
