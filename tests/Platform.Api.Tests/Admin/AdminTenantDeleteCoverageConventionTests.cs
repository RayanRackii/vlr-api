using Microsoft.EntityFrameworkCore;
using Platform.Api.Tests.Fakes;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

public sealed class AdminTenantDeleteCoverageConventionTests
{
    private static readonly HashSet<string> KnownGaps = [];

    [Fact]
    public void Every_model_entity_with_TenantId_is_deleted_or_is_an_explicit_known_gap()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                // Build the production relational model (including JSONB converters) without
                // opening a connection or requiring a running PostgreSQL instance.
                .UseNpgsql("Host=localhost;Database=model_only;Username=postgres;Password=postgres")
                .Options,
            new FakeTenantProvider());

        var deleteMethod = ReadDeleteMethod();
        var entities = db.Model.GetEntityTypes()
            .Where(entity => entity.FindProperty("TenantId") is not null)
            .ToDictionary(entity => entity.ClrType.Name, StringComparer.Ordinal);

        var covered = entities.Values
            .Where(entity => deleteMethod.Contains(
                $"dbContext.{DbSetName(entity.ClrType)}",
                StringComparison.Ordinal))
            .Select(entity => entity.ClrType.Name)
            .ToHashSet(StringComparer.Ordinal);

        var newlyUncovered = entities.Keys
            .Except(covered, StringComparer.Ordinal)
            .Except(KnownGaps, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var gapsAlreadyCovered = KnownGaps
            .Intersect(covered, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var staleGaps = KnownGaps
            .Except(entities.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(newlyUncovered.Length == 0 && gapsAlreadyCovered.Length == 0 && staleGaps.Length == 0,
            $"Tenant delete coverage drifted. Unlisted gaps: [{string.Join(", ", newlyUncovered)}]; " +
            $"known gaps now covered: [{string.Join(", ", gapsAlreadyCovered)}]; " +
            $"known gaps absent from model: [{string.Join(", ", staleGaps)}].");
    }

    private static string ReadDeleteMethod()
    {
        var root = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
            .SelectMany(StartDirectories)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "Platform.Api", "Platform.Api.csproj")));
        Assert.NotNull(root);

        var source = File.ReadAllText(Path.Combine(
            root!, "Platform.Api", "Modules", "Admin", "Services", "AdminTenantService.cs"));
        const string startMarker = "public async Task DeleteAsync(";
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {startMarker} in AdminTenantService.cs.");
        var end = source.IndexOf("public async Task<EnterTenantEnvironmentResponseDto>", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not find the end of AdminTenantService.DeleteAsync.");
        return source[start..end];
    }

    private static IEnumerable<string> StartDirectories(string path)
    {
        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private static string DbSetName(Type entityType) =>
        typeof(AppDbContext).GetProperties()
            .Single(property => property.PropertyType.IsGenericType
                && property.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)
                && property.PropertyType.GenericTypeArguments[0] == entityType)
            .Name;
}
