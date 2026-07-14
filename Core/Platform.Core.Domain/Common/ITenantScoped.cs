namespace Platform.Core.Domain.Common;

public interface ITenantScoped
{
    Guid TenantId { get; }
}
