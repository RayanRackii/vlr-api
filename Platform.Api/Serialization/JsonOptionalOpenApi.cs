using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Platform.Api.Serialization;

/// <summary>
/// Documents <see cref="JsonOptional{T}"/> of <see cref="int"/> as integer|null.
/// The wire value is a number or null; the presence flag is not part of the schema.
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
        options.SchemaFilter<JsonOptionalIntSchemaFilter>();
    }

    /// <summary>
    /// Swashbuckle clears the null flag on a non-nullable struct after <c>MapType</c>.
    /// Put it back so the documented wire type stays integer|null.
    /// </summary>
    public sealed class JsonOptionalIntSchemaFilter : ISchemaFilter
    {
        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            if (context.Type != typeof(JsonOptional<int>) || schema is not OpenApiSchema concrete)
            {
                return;
            }

            concrete.Type = JsonSchemaType.Integer | JsonSchemaType.Null;
            concrete.Format = "int32";
        }
    }
}
