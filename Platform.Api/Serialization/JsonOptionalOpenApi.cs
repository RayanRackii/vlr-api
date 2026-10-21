using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Platform.Api.Serialization;

/// <summary>
/// Documents optional scalar values as integer|null or uuid|null.
/// The presence flag is not part of the wire schema.
/// </summary>
public static class JsonOptionalOpenApi
{
    public static void Configure(SwaggerGenOptions options)
    {
        options.MapType<JsonOptional<int>>(() => new OpenApiSchema
        {
            Type = JsonSchemaType.Integer | JsonSchemaType.Null,
            Format = "int32",
        });
        options.MapType<JsonOptional<Guid>>(() => new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
            Format = "uuid",
        });
        options.SchemaFilter<JsonOptionalIntSchemaFilter>();
    }

    /// <summary>
    /// Swashbuckle clears the null flag on a non-nullable struct after <c>MapType</c>.
    /// Put it back so the documented wire type retains explicit null support.
    /// </summary>
    public sealed class JsonOptionalIntSchemaFilter : ISchemaFilter
    {
        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            if (schema is not OpenApiSchema concrete)
            {
                return;
            }

            if (context.Type == typeof(JsonOptional<int>))
            {
                concrete.Type = JsonSchemaType.Integer | JsonSchemaType.Null;
                concrete.Format = "int32";
            }
            else if (context.Type == typeof(JsonOptional<Guid>))
            {
                concrete.Type = JsonSchemaType.String | JsonSchemaType.Null;
                concrete.Format = "uuid";
            }
        }
    }
}
