using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Common;
using Platform.Api.Modules.Customers.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Customers.Controllers;

[ApiController]
[Authorize]
[Route("api/customers")]
public sealed class TenantCustomersController(
    ITenantCustomerDirectory tenantCustomerDirectory,
    ITenantProvider tenantProvider,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DirectoryPage.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var denied = await DirectoryAuthorization.GateAsync(
            this,
            tenantProvider,
            authorizationService,
            User,
            Permissions.Core.CustomersRead);
        if (denied is not null)
        {
            return denied;
        }

        if (!DirectoryPage.TryCreate(q, page, pageSize, out var directoryPage, out var error))
        {
            return BadRequest(new { error });
        }

        var customers = await tenantCustomerDirectory.ListAsync(directoryPage, cancellationToken);
        return Ok(customers);
    }
}
