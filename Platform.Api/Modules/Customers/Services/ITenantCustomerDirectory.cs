using Platform.Api.Common;
using Platform.Api.Modules.Customers.Dtos;

namespace Platform.Api.Modules.Customers.Services;

public interface ITenantCustomerDirectory
{
    Task<PagedResult<TenantCustomerListItemDto>> ListAsync(
        DirectoryPage page,
        CancellationToken cancellationToken);
}
