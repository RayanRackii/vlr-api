using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/public/tenants/{subdomain}/availability/days")]
public sealed class DailyAvailabilityController(
    IDailyAvailabilityService availability, IPublicTenantBinder tenantBinder) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("{date}")]
    [ProducesResponseType<DailyAvailabilityResponseDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DailyAvailabilityResponseDto>> GetDay(string subdomain, string date,
        [FromQuery] string[]? rentalAssetIds, [FromQuery] string? unitId,
        [FromQuery] string? quantity, [FromQuery] string? durationMinutes, CancellationToken cancellationToken)
    {
        try
        {
            // Parse explicitly so malformed input follows this endpoint's stable public error contract.
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                || !TryInteger(quantity, 1, out var count) || !TryInteger(durationMinutes, null, out var duration)
                || (unitId is not null && !Guid.TryParse(unitId, out _)))
                throw InvalidQuery();
            // Support both repeated rentalAssetIds and the documented rentalAssetIds[] query spelling.
            var rawIds = (rentalAssetIds ?? []).Concat(Request.Query["rentalAssetIds[]"].Select(s => s ?? "")).ToArray();
            var ids = new List<Guid>();
            foreach (var raw in rawIds)
            {
                if (!Guid.TryParse(raw, out var id)) throw InvalidQuery();
                ids.Add(id);
            }
            await tenantBinder.BindFromSubdomainAsync(subdomain, cancellationToken);
            return Ok(await availability.GetAsync(day, ids, unitId is null ? null : Guid.Parse(unitId),
                count!.Value, duration, cancellationToken));
        }
        catch (BookingHorizonExceededException ex)
        {
            return BadRequest(new { error = ex.Message, code = BookingHorizonExceededException.Code });
        }
        catch (AvailabilityRequestException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        catch (TenantResolutionException)
        {
            return NotFound(new { error = "Tenant not found.", code = "TENANT_NOT_FOUND" });
        }
    }

    private static bool TryInteger(string? value, int? fallback, out int? result)
    {
        result = fallback;
        if (value is null) return true;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return false;
        result = parsed;
        return true;
    }
    private static AvailabilityRequestException InvalidQuery() =>
        new("AVAILABILITY_INVALID_QUERY", "Invalid availability query.");
}
