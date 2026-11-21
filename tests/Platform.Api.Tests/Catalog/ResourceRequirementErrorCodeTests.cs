using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Taxonomy.Controllers;

namespace Platform.Api.Tests.Catalog;

public sealed class ResourceRequirementErrorCodeTests
{
    private static ActionExecutingContext Context(IList<object> metadata, string? modelError)
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor { EndpointMetadata = metadata });
        if (modelError is not null)
            actionContext.ModelState.AddModelError("Requirements", modelError);
        return new ActionExecutingContext(
            actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    private static (string? Error, string? Code) Read(ActionExecutingContext ctx)
    {
        var result = Assert.IsType<BadRequestObjectResult>(ctx.Result);
        var value = result.Value!;
        var type = value.GetType();
        return ((string?)type.GetProperty("error")!.GetValue(value), (string?)type.GetProperty("code")!.GetValue(value));
    }

    [Fact]
    public void Replace_requirements_action_declares_its_own_model_state_code()
    {
        var method = typeof(CatalogProductsController).GetMethod(nameof(CatalogProductsController.ReplaceResourceRequirements))!;
        var attribute = method.GetCustomAttribute<ModelStateErrorCodeAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal("catalog.resource_requirement_invalid", attribute!.Code);
    }

    [Fact]
    public void Invalid_model_with_override_returns_specific_code_and_message()
    {
        var ctx = Context([new ModelStateErrorCodeAttribute("catalog.resource_requirement_invalid")], "resourceQuantity must be at least 1.");
        new TaxonomyErrorsAttribute().OnActionExecuting(ctx);

        var (error, code) = Read(ctx);
        Assert.Equal("catalog.resource_requirement_invalid", code);
        Assert.Equal("resourceQuantity must be at least 1.", error);
    }

    [Fact]
    public void Invalid_model_without_override_keeps_taxonomy_code()
    {
        var ctx = Context([], "bad");
        new TaxonomyErrorsAttribute().OnActionExecuting(ctx);

        var (_, code) = Read(ctx);
        Assert.Equal("TAXONOMY_INVALID", code);
    }

    [Fact]
    public void Valid_model_does_not_short_circuit()
    {
        var ctx = Context([new ModelStateErrorCodeAttribute("x")], null);
        new TaxonomyErrorsAttribute().OnActionExecuting(ctx);
        Assert.Null(ctx.Result);
    }
}
