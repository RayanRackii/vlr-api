using Microsoft.EntityFrameworkCore;
using Platform.Api.Common;
using Platform.Api.Modules.Customers.Dtos;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Customers.Services;

public sealed class TenantCustomerDirectory(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : ITenantCustomerDirectory
{
    public async Task<PagedResult<TenantCustomerListItemDto>> ListAsync(
        DirectoryPage page,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var query = dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId);

        if (page.Term is not null)
        {
            var pattern = DirectoryPage.ToContainsPattern(page.Term);
            query = query.Where(customer =>
                EF.Functions.ILike(customer.Name, pattern, DirectoryPage.LikeEscape)
                || (customer.Email != null
                    && EF.Functions.ILike(customer.Email, pattern, DirectoryPage.LikeEscape)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(customer => customer.Name)
            .ThenBy(customer => customer.Id)
            .Skip((page.Page - 1) * page.PageSize)
            .Take(page.PageSize)
            .Select(customer => new TenantCustomerListItemDto(
                customer.Id,
                customer.Name,
                customer.Email,
                customer.EmailVerifiedAt != null,
                customer.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<TenantCustomerListItemDto>(items, page.Page, page.PageSize, total);
    }
}
