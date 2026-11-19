using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Assets;

public sealed class AssetFamilyConcurrencyPostgresTests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private const string Schema =
        """{"fields":[{"key":"surface","type":"text","required":false,"label":"Surface"}],"version":1}""";

    [DockerFact]
    public async Task Loading_and_saving_a_family_unchanged_does_not_conflict()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        var provider = new FakeTenantProvider();
        var familyId = await SeedFamilyAsync(factory, provider);

        await using var db = factory.Create(provider);
        var service = new AssetFamilyService(db);
        var current = await service.GetByIdAsync(familyId, CancellationToken.None);

        var saved = await service.UpdateAsync(
            familyId,
            new UpdateAssetFamilyRequest("Fam", current.Fields, current.SortOrder, true, ExpectedSchemaVersion: current.SchemaVersion),
            CancellationToken.None);

        Assert.Equal(current.SchemaVersion, saved.SchemaVersion);
    }

    [DockerFact]
    public async Task Second_context_saving_after_a_real_change_gets_a_conflict()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        var provider = new FakeTenantProvider();
        var familyId = await SeedFamilyAsync(factory, provider);

        await using var first = factory.Create(provider);
        await using var second = factory.Create(provider);

        // Both contexts load (and track) the family at version 1.
        await first.AssetFamilies.SingleAsync(f => f.Id == familyId);
        await second.AssetFamilies.SingleAsync(f => f.Id == familyId);

        await new AssetFamilyService(first).UpdateAsync(
            familyId,
            new UpdateAssetFamilyRequest(
                "Fam",
                [new AssetFamilyFieldDto("surface", "text", false, "Surface"), new AssetFamilyFieldDto("extra", "text", false, "Extra")],
                1,
                true),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AssetFamilyService(second).UpdateAsync(
                familyId,
                new UpdateAssetFamilyRequest(
                    "Fam",
                    [new AssetFamilyFieldDto("surface", "text", false, "Surface"), new AssetFamilyFieldDto("other", "text", false, "Other")],
                    1,
                    true),
                CancellationToken.None));
        Assert.Contains("changed since it was loaded", ex.Message, StringComparison.Ordinal);

        await using var verify = factory.Create(provider);
        var stored = await verify.AssetFamilies.AsNoTracking().SingleAsync(f => f.Id == familyId);
        Assert.Contains("\"extra\"", stored.FieldSchemaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"other\"", stored.FieldSchemaJson, StringComparison.Ordinal);
    }

    private static async Task<Guid> SeedFamilyAsync(PostgresAppDbFactory factory, FakeTenantProvider provider)
    {
        await using var db = factory.Create(provider);
        var family = new AssetFamily
        {
            Key = "conc-" + Guid.NewGuid().ToString("N")[..20],
            Label = "Fam",
            FieldSchemaJson = Schema,
            SortOrder = 1,
            IsActive = true,
        };
        db.AssetFamilies.Add(family);
        await db.SaveChangesAsync();
        return family.Id;
    }
}
