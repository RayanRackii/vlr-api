using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Platform.Api.Http;

/// <summary>
/// Keeps framework and serializer error text (which names internal .NET types) out of 400 bodies.
/// The response shape stays the default <see cref="ValidationProblemDetails"/>.
/// </summary>
public static partial class ApiErrorSanitizer
{
    public const string GenericInvalidMessage = "Requisição inválida.";

    private const string DefaultFrameworkMessage = "The input was not valid.";

    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return DefaultFrameworkMessage;
        }

        var missing = MissingRequiredRegex().Match(message);
        if (missing.Success)
        {
            var names = missing.Groups["names"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.Trim('\'', '"', ' ', '.'))
                .Where(name => name.Length > 0)
                .ToArray();
            return names.Length == 0
                ? "Campo obrigatório ausente."
                : $"Campo obrigatório ausente: {string.Join(", ", names)}";
        }

        var convert = NotConvertedRegex().Match(message);
        if (convert.Success)
        {
            var field = LastPathSegment(convert.Groups["path"].Value);
            return string.IsNullOrEmpty(field)
                ? "Valor inválido."
                : $"Valor inválido para o campo: {field}";
        }

        return LeaksTypeNames(message) ? GenericInvalidMessage : message;
    }

    public static ModelStateDictionary Sanitize(ModelStateDictionary source)
    {
        var sanitized = new ModelStateDictionary();
        foreach (var (key, entry) in source)
        {
            if (entry is null || entry.Errors.Count == 0)
            {
                continue;
            }

            foreach (var error in entry.Errors)
            {
                var raw = !string.IsNullOrEmpty(error.ErrorMessage)
                    ? error.ErrorMessage
                    : error.Exception?.Message;
                sanitized.AddModelError(key, Sanitize(raw));
            }
        }

        return sanitized;
    }

    public static IActionResult BuildInvalidModelStateResult(
        ActionContext context,
        ProblemDetailsFactory problemDetailsFactory)
    {
        var details = problemDetailsFactory.CreateValidationProblemDetails(
            context.HttpContext,
            Sanitize(context.ModelState),
            statusCode: StatusCodes.Status400BadRequest);
        return new BadRequestObjectResult(details)
        {
            ContentTypes = { "application/problem+json", "application/problem+xml" },
        };
    }

    public static IMvcBuilder AddSanitizedModelStateErrors(this IMvcBuilder builder) =>
        builder.ConfigureApiBehaviorOptions(options =>
            options.InvalidModelStateResponseFactory = context =>
                BuildInvalidModelStateResult(
                    context,
                    context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>()));

    private static bool LeaksTypeNames(string message) =>
        TypeNameRegex().IsMatch(message) || message.Contains("LineNumber:", StringComparison.Ordinal);

    private static string LastPathSegment(string path)
    {
        var trimmed = path.TrimEnd('.', '|', ' ');
        var index = trimmed.LastIndexOf('.');
        return index >= 0 ? trimmed[(index + 1)..] : trimmed.TrimStart('$');
    }

    [GeneratedRegex(@"missing required properties[^:]*:\s*(?<names>.+?)\.?\s*$", RegexOptions.Singleline)]
    private static partial Regex MissingRequiredRegex();

    [GeneratedRegex(@"could not be converted to \S+?\.?\s+Path:\s*(?<path>[^\s|]+)")]
    private static partial Regex NotConvertedRegex();

    [GeneratedRegex(@"\b(?:Platform|System|Microsoft)\.[A-Za-z0-9_.`+\[\],]+")]
    private static partial Regex TypeNameRegex();
}
