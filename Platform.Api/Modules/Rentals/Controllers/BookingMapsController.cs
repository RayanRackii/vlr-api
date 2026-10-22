using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Controllers;

/// <summary>Keeps malformed JSON/model errors within the new map error contract.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BookingMapModelValidationAttribute : ActionFilterAttribute
{
    public BookingMapModelValidationAttribute() { Order = -3000; }
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = new BadRequestObjectResult(new { code = "MAP_INVALID", error = "Invalid map request." });
    }
}

[ApiController]
[BookingMapModelValidation]
[RequireActiveModule(PlatformModules.Rentals)]
public sealed class BookingMapsController(IRentalLayoutService maps) : ControllerBase
{
    [RequirePermission(Permissions.Rentals.LayoutsRead)]
    [HttpGet("api/rental-layouts/{id:guid}/composition")]
    public async Task<IActionResult> GetComposition(Guid id, CancellationToken ct)
    {
        var result = await maps.GetCompositionAsync(id, ct);
        return result is null ? NotFound(new { code = "MAP_NOT_FOUND", error = "Map was not found." }) : Ok(result);
    }
    [RequirePermission(Permissions.Rentals.LayoutsWrite)]
    [HttpPut("api/rental-layouts/{id:guid}/composition")]
    public Task<IActionResult> SaveComposition(Guid id, SaveBookingMapCompositionRequest request, CancellationToken ct) =>
        RunAsync(async () => Ok(await maps.SaveCompositionAsync(id, request, ct)));
    [RequirePermission(Permissions.Rentals.MapSettingsRead)]
    [HttpGet("api/rentals/booking-map-settings")]
    public Task<IActionResult> GetSettings(CancellationToken ct) => RunAsync(async () => Ok(await maps.GetSettingsAsync(ct)));
    [RequirePermission(Permissions.Rentals.MapSettingsWrite)]
    [HttpPut("api/rentals/booking-map-settings")]
    public Task<IActionResult> SaveSettings(SaveBookingMapSettingsRequest request, CancellationToken ct) =>
        RunAsync(async () => Ok(await maps.SaveSettingsAsync(request, ct)));
    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (BookingMapException ex) { return StatusCode(ex.StatusCode, new { code = ex.Code, error = ex.Message }); }
    }
}

[ApiController]
[BookingMapModelValidation]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/public/tenants/{subdomain}/booking-maps")]
public sealed class PublicBookingMapsController(IRentalLayoutService maps, IPublicTenantBinder binder,
    ITenantProvider tenants, IOptions<BookingUiOptions> options) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet]
    public Task<IActionResult> ListPublic(string subdomain, [FromQuery] string? unitId, CancellationToken ct) =>
        RunAsync(subdomain, unitId, async unit => await maps.ListPublicMapsAsync(unit, ct), ct);
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public Task<IActionResult> GetPublic(string subdomain, Guid id, [FromQuery] string? unitId, CancellationToken ct) =>
        RunAsync(subdomain, unitId, async unit => await maps.GetPublicMapAsync(id, unit, ct), ct);
    private async Task<IActionResult> RunAsync(string subdomain, string? rawUnit,
        Func<Guid?, Task<object?>> action, CancellationToken ct)
    {
        try
        {
            if (rawUnit is not null && (!Guid.TryParse(rawUnit, out var unit) || unit == Guid.Empty))
                throw BookingMapValidation.Invalid("Invalid unit id.");
            await binder.BindFromSubdomainAsync(subdomain, ct);
            if (tenants.TenantId is not { } tenant || !options.Value.IsEnabled(tenant))
                return NotFound(new { code = "BOOKING_MAPS_DISABLED", error = "Booking maps are not enabled." });
            var dto = await action(rawUnit is null ? null : Guid.Parse(rawUnit));
            if (dto is null) return NotFound(new { code = "MAP_NOT_FOUND", error = "Map was not found." });
            // Includes assignment/resource activation and settings changes, not just layout version.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, dto.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\"";
            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
            if (Request.Headers.IfNoneMatch.Any(h => (h ?? "").Split(',').Any(v => v.Trim() == etag || v.Trim() == "W/" + etag || v.Trim() == "*")))
                return StatusCode(StatusCodes.Status304NotModified);
            return Ok(dto);
        }
        catch (TenantResolutionException) { return NotFound(new { code = "TENANT_NOT_FOUND", error = "Tenant was not found." }); }
        catch (BookingMapException ex) { return StatusCode(ex.StatusCode, new { code = ex.Code, error = ex.Message }); }
    }
}

