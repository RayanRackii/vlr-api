using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Text.Json;

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
        options.MapType<JsonOptional<bool>>(() => new OpenApiSchema
        {
            Type = JsonSchemaType.Boolean | JsonSchemaType.Null,
        });
        options.MapType<JsonOptionalString>(() => new OpenApiSchema
        {
            Type = JsonSchemaType.String | JsonSchemaType.Null,
        });
        options.SchemaFilter<JsonOptionalIntSchemaFilter>();
        options.SchemaFilter<JsonOptionalPropertySchemaFilter>();
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

    /// <summary>
    /// Property-level System.Text.Json converters are expanded by Swashbuckle before
    /// the scalar type mapping is considered. Replace those expanded presence
    /// objects on their containing DTO so OpenAPI matches the JSON wire value.
    /// </summary>
    public sealed class JsonOptionalPropertySchemaFilter : ISchemaFilter
    {
        public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
        {
            if (schema is not OpenApiSchema concrete)
            {
                return;
            }

            var properties = concrete.Properties;
            if (properties is null)
            {
                return;
            }

            foreach (var property in context.Type.GetProperties())
            {
                OpenApiSchema replacement;
                if (property.PropertyType == typeof(JsonOptional<int>))
                {
                    replacement = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Integer | JsonSchemaType.Null,
                        Format = "int32",
                    };
                }
                else if (property.PropertyType == typeof(JsonOptional<bool>))
                {
                    replacement = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Boolean | JsonSchemaType.Null,
                    };
                }
                else if (property.PropertyType == typeof(JsonOptionalString))
                {
                    replacement = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String | JsonSchemaType.Null,
                    };
                }
                else
                {
                    continue;
                }

                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                if (!properties.ContainsKey(name))
                {
                    continue;
                }

                properties[name] = replacement;
            }
        }
    }
}
