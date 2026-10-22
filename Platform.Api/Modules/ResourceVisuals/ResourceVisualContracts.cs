using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.ResourceVisuals;

public sealed class ResourceVisualException(string code, string error, int status = 400) : Exception(error)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
public sealed class ResourceVisualExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ResourceVisualException error) return;
        context.Result = new ObjectResult(new { code = error.Code, error = error.Message }) { StatusCode = error.Status };
        context.ExceptionHandled = true;
    }
}

public sealed class ResourceVisualErrorsAttribute : ActionFilterAttribute
{
    public ResourceVisualErrorsAttribute() => Order = -3000;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid) return;
        var code = context.HttpContext.Request.Path.Value?.EndsWith("/resource-segments", StringComparison.OrdinalIgnoreCase) == true
            ? "SEGMENT_INVALID" : "VISUAL_TEMPLATE_INVALID";
        context.Result = new BadRequestObjectResult(new { code, error = "Invalid visual catalog request." });
    }
}
public sealed record TemplateRequest(string Slug, Dictionary<string, string?> NameI18n, Dictionary<string, string?> DescriptionI18n, Guid[] TagIds, bool IsActive = true, int SortOrder = 0, long? ExpectedVersion = null);
public sealed record TagRequest(string Slug, ResourceVisualTagKind Kind, string Facet, Dictionary<string, string?> NameI18n, bool IsActive = true, int SortOrder = 0);
public sealed record RevisionRequest(
    string FloorSvg,
    string? LinesSvg,
    string? NetSvg,
    string ViewBox,
    decimal AspectRatio,
    string DefaultColor,
    string DefaultIcon,
    int? SuggestedSlotMinutes = 60,
    int? SuggestedCapacityPeople = null,
    SchedulePolicy? DefaultSchedulePolicy = null,
    TimeOnly? DefaultOpenTime = null,
    TimeOnly? DefaultCloseTime = null,
    string? DefaultAllowedDurationMinutes = null,
    IReadOnlyList<CreateRentalPricingDto>? DefaultPricings = null,
    bool? DefaultRequiresDeposit = null,
    int? DefaultMaxAdvanceBookingDays = null,
    long? ExpectedVersion = null);
public sealed record SegmentsRequest(Guid[] SegmentIds);
public sealed record VisualPickerQuery(string? Search, Guid[]? SegmentIds, Guid[]? TagIds, bool AllSegments = false, int Page = 1, int PageSize = 30);
public sealed record VisualTagDto(Guid Id, string Slug, ResourceVisualTagKind Kind, string Facet, Dictionary<string, string?> NameI18n, bool IsActive, int SortOrder);
public sealed record VisualRevisionDto(
    Guid Id,
    Guid TemplateId,
    int RevisionNo,
    ResourceVisualRevisionStatus Status,
    string ViewBox,
    decimal AspectRatio,
    string DefaultColor,
    string DefaultIcon,
    int? SuggestedSlotMinutes,
    int? SuggestedCapacityPeople,
    SchedulePolicy? DefaultSchedulePolicy,
    TimeOnly? DefaultOpenTime,
    TimeOnly? DefaultCloseTime,
    string? DefaultAllowedDurationMinutes,
    IReadOnlyList<CreateRentalPricingDto>? DefaultPricings,
    bool? DefaultRequiresDeposit,
    int? DefaultMaxAdvanceBookingDays,
    string ArtSha256,
    string? ArtUrl,
    long Version);
public sealed record VisualTemplateDto(Guid Id, string Slug, Dictionary<string, string?> NameI18n, Dictionary<string, string?> DescriptionI18n, bool IsActive, int SortOrder, long Version, IReadOnlyList<VisualTagDto> Tags, VisualRevisionDto? PublishedRevision);
public sealed record VisualPage(IReadOnlyList<VisualTemplateDto> Items, int Total, int Page, int PageSize, string FilterSemantics = "OR within each facet; AND between facets; AND for each search term. Tenant segments apply when segmentIds is empty and allSegments is false.");

public sealed record AdminVisualRevisionDto(VisualRevisionDto Metadata, string FloorSvg, string? LinesSvg, string? NetSvg, DateTimeOffset? PublishedAt)
{
    public static AdminVisualRevisionDto From(Platform.Core.Domain.Entities.ResourceVisualTemplateRevision revision) => new(ResourceVisualCatalogService.RevisionDto(revision), revision.FloorSvg, revision.LinesSvg, revision.NetSvg, revision.PublishedAt);
}
