using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Catalog)]
public sealed class CustomerGroupsController(ICustomerGroupService service) : ControllerBase
{
    [HttpGet("api/catalog/customer-groups")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<IReadOnlyList<CustomerGroupResponse>>> List(CancellationToken ct) =>
        Ok(await service.ListAsync(ct));

    [HttpGet("api/catalog/customer-groups/{id:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<CustomerGroupResponse>> Get(Guid id, CancellationToken ct) =>
        await service.GetAsync(id, ct) is { } g ? Ok(g) : NotFound();

    [HttpPost("api/catalog/customer-groups")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public Task<ActionResult<CustomerGroupResponse>> Create([FromBody] UpsertCustomerGroupRequest request, CancellationToken ct) =>
        Guard<CustomerGroupResponse>(async () => { var g = await service.CreateAsync(request, ct); return CreatedAtAction(nameof(Get), new { id = g.Id }, g); });

    [HttpPut("api/catalog/customer-groups/{id:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public Task<ActionResult<CustomerGroupResponse>> Update(Guid id, [FromBody] UpsertCustomerGroupRequest request, CancellationToken ct) =>
        Guard<CustomerGroupResponse>(async () => await service.UpdateAsync(id, request, ct) is { } g ? Ok(g) : NotFound());

    [HttpDelete("api/catalog/customer-groups/{id:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        await service.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpPost("api/catalog/customer-groups/{id:guid}/members")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public Task<ActionResult<CustomerGroupResponse>> AddMembers(Guid id, [FromBody] CustomerGroupMembersRequest request, CancellationToken ct) =>
        Guard<CustomerGroupResponse>(async () => await service.AddMembersAsync(id, request, ct) is { } g ? Ok(g) : NotFound());

    [HttpDelete("api/catalog/customer-groups/{id:guid}/members/{customerId:guid}")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public async Task<ActionResult<CustomerGroupResponse>> RemoveMember(Guid id, Guid customerId, CancellationToken ct) =>
        await service.RemoveMemberAsync(id, customerId, ct) is { } g ? Ok(g) : NotFound();

    [HttpGet("api/catalog/products/{id:guid}/access")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<CatalogProductAccessResponse>> GetAccess(Guid id, CancellationToken ct) =>
        await service.GetProductAccessAsync(id, ct) is { } a ? Ok(a) : NotFound();

    [HttpPut("api/catalog/products/{id:guid}/access")]
    [RequirePermission(Permissions.Catalog.ProductsManage)]
    public Task<ActionResult<CatalogProductAccessResponse>> SetAccess(Guid id, [FromBody] UpdateCatalogProductAccessRequest request, CancellationToken ct) =>
        Guard<CatalogProductAccessResponse>(async () => await service.SetProductAccessAsync(id, request, ct) is { } a ? Ok(a) : NotFound());

    [HttpGet("api/catalog/customer-groups/{id:guid}/history")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogAccessAuditResponse>>> GroupHistory(Guid id, CancellationToken ct) =>
        Ok(await service.ListHistoryAsync("group", id, ct));

    [HttpGet("api/catalog/products/{id:guid}/access/history")]
    [RequirePermission(Permissions.Catalog.ProductsRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogAccessAuditResponse>>> AccessHistory(Guid id, CancellationToken ct) =>
        Ok(await service.ListHistoryAsync("product", id, ct));

    private async Task<ActionResult<T>> Guard<T>(Func<Task<ActionResult>> action)
    {
        try { return await action(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (CatalogVisibilityFilesPresentException ex)
        {
            return Conflict(new { error = ex.Message, code = CatalogVisibilityFilesPresentException.Code });
        }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
