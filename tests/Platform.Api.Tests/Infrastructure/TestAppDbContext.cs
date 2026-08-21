using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Test-only AppDbContext that remaps Postgres jsonb/default SQL for InMemory and SQLite.
/// </summary>
internal sealed class TestAppDbContext : AppDbContext
{
    public TestAppDbContext(DbContextOptions<AppDbContext> options, ITenantProvider tenantProvider)
        : base(options, tenantProvider)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        TestProviderModelAdjustments.Apply(modelBuilder);
    }
}

internal static class TestProviderModelAdjustments
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private static readonly ValueConverter<Dictionary<string, string?>, string> DictionaryConverter =
        new(
            value => JsonSerializer.Serialize(value, JsonOptions),
            json => JsonSerializer.Deserialize<Dictionary<string, string?>>(json, JsonOptions)
                ?? new Dictionary<string, string?>(StringComparer.Ordinal));

    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            entityType.SetSchema(null);

            foreach (var property in entityType.GetProperties())
            {
                if (IsStringDictionary(property.ClrType))
                {
                    property.SetValueConverter(DictionaryConverter);
                    property.SetColumnType("TEXT");
                }
                else if (string.Equals(property.GetColumnType(), "jsonb", StringComparison.OrdinalIgnoreCase))
                {
                    property.SetColumnType("TEXT");
                }

                if (!string.IsNullOrEmpty(property.GetDefaultValueSql()))
                {
                    property.SetDefaultValueSql(null);
                }
            }
        }
    }

    private static bool IsStringDictionary(Type clrType) =>
        clrType.IsGenericType
        && clrType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
        && clrType.GenericTypeArguments[0] == typeof(string)
        && clrType.GenericTypeArguments[1] == typeof(string);
}
