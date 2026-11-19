using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Assets;

public sealed class AssetAttributesPostgresTests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    private const string Schema =
        """{"fields":[{"key":"capacity","type":"number","required":false},{"key":"covered","type":"boolean","required":false},{"key":"model","type":"text","required":false}]}""";

    private const string LegacyJson = """{"capacity":"12","covered":"true","model":null,"old_key":"x"}""";

    [DockerFact]
    public async Task Legacy_jsonb_row_reads_as_strings_and_is_upgraded_on_update_keeping_unknown_keys()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        var provider = new FakeTenantProvider();
        Guid assetId, unitId, categoryId, familyId;

        await using (var db = factory.Create(provider))
        {
            var tenant = new Tenant("Attributes test", Guid.NewGuid().ToString("N")[..20], subdomain: "attrs-" + Guid.NewGuid().ToString("N"));
            provider.TenantId = tenant.Id;
            var unit = new Unit(tenant.Id, "Site");
            var family = new AssetFamily { Key = "attrs-" + Guid.NewGuid().ToString("N")[..20], Label = "Spaces", FieldSchemaJson = Schema };
            var category = new AssetCategory { TenantId = tenant.Id, Name = "Resources" };
            db.AddRange(tenant, unit, family, category, new TenantAssetFamily(tenant.Id, family.Id));
            await db.SaveChangesAsync();
            (unitId, categoryId, familyId) = (unit.Id, category.Id, family.Id);

            var created = await new AssetService(db, provider, new FakeTrialGuard(), new RentalConfigurationSync(db)).CreateRentableAsync(
                new CreateRentableRequest { Name = "Resource", Tag = "A1", UnitId = unitId, CategoryId = categoryId, FamilyId = familyId },
                CancellationToken.None);
            assetId = created.Id;

            // Simulates a row written by the old dynamic-JSON mapping.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE assets.assets SET attributes = CAST({0} AS jsonb) WHERE id = {1}", LegacyJson, assetId);
        }

        await using (var read = factory.Create(provider))
        {
            var service = new AssetService(read, provider, new FakeTrialGuard(), new RentalConfigurationSync(read));
            var asset = await service.GetByIdAsync(assetId, CancellationToken.None);
            Assert.Equal("12", asset!.Attributes["capacity"].GetString());
            Assert.Equal("true", asset.Attributes["covered"].GetString());
            Assert.Equal("x", asset.Attributes["old_key"].GetString());
            Assert.DoesNotContain("model", asset.Attributes.Keys);

            var rental = await new RentalAssetService(read, provider).GetByAssetIdAsync(assetId, CancellationToken.None);
            Assert.Equal("12", rental!.Attributes!["capacity"].GetString());
            Assert.DoesNotContain("model", rental.Attributes.Keys);
        }

        await using (var write = factory.Create(provider))
        {
            var service = new AssetService(write, provider, new FakeTrialGuard(), new RentalConfigurationSync(write));
            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["capacity"] = JsonSerializer.SerializeToElement("12"),
                ["covered"] = JsonSerializer.SerializeToElement("true"),
            };
            await service.UpdateRentableAsync(
                assetId,
                new UpdateRentableRequest
                {
                    Name = "Resource",
                    Tag = "A1",
                    UnitId = unitId,
                    CategoryId = categoryId,
                    FamilyId = familyId,
                    RentalType = Platform.Core.Domain.Enums.RentalAssetType.Location,
                    TotalQuantity = 1,
                    Attributes = payload,
                },
                CancellationToken.None);
        }

        await using (var verify = factory.Create(provider))
        {
            var stored = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
            Assert.Equal(JsonValueKind.Number, stored.Attributes["capacity"].ValueKind);
            Assert.Equal(12m, stored.Attributes["capacity"].GetDecimal());
            Assert.Equal(JsonValueKind.True, stored.Attributes["covered"].ValueKind);
            Assert.Equal("x", stored.Attributes["old_key"].GetString());
            Assert.DoesNotContain("model", stored.Attributes.Keys);
        }
    }
}
