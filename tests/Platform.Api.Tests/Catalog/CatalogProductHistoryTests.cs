using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Taxonomy.Dtos;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogProductHistoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private sealed class Rig(CatalogHarness h, CatalogProductService products, CatalogProductHistoryService history) : IAsyncDisposable
    {
        public CatalogHarness H { get; } = h;
        public CatalogProductService Products { get; } = products;
        public CatalogProductHistoryService History { get; } = history;

        public async Task<IReadOnlyList<CatalogProductHistoryEntryResponse>> ListAsync(Guid productId) =>
            (await History.ListAsync(productId, 50, null, Ct))!;

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static async Task<Rig> CreateRigAsync()
    {
        var h = await CatalogHarness.CreateAsync();
        var user = new User(h.Tenant.Id, Guid.NewGuid().ToString("N"), "Rita Ops", $"{Guid.NewGuid():N}@ops.test");
        h.Db.Users.Add(user);
        await h.Db.SaveChangesAsync();

        var http = new FakeHttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", user.SupabaseAuthId) }, "test")),
            },
        };
        var history = new CatalogProductHistoryService(h.Db, h.TenantProvider, http);
        var options = Options.Create(new StorageOptions());
        var storage = new DevStorageProvider(new FakeHostEnvironment(), options);
        var products = new CatalogProductService(h.Db, h.TenantProvider, storage, options, null, history);
        return new Rig(h, products, history);
    }

    private static UpdateCatalogProductRequest Update(
        string name = "Item",
        string? code = "C1",
        decimal? price = 10m,
        TaxonomyAssignmentRequest? taxonomy = null) =>
        new() { Name = name, Code = code, Price = price, Currency = "BRL", Taxonomy = taxonomy };

    private static async Task<CatalogProductResponse> CreateProductAsync(Rig r) =>
        await r.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Code = "C1", Price = 10m, UsesSchedule = false }, Ct);

    [Fact]
    public async Task Create_writes_one_created_entry_with_actor()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);

        var entry = Assert.Single(await r.ListAsync(product.Id));
        Assert.Equal(CatalogProductHistoryActions.Created, entry.Action);
        Assert.Equal("Rita Ops", entry.ActorName);
        Assert.Contains(entry.Changes, c => c.Field == "name" && c.Old is null && c.New == "Item");
        Assert.Contains(entry.Changes, c => c.Field == "price" && c.New == "10.00");
    }

    [Fact]
    public async Task Update_records_only_the_changed_fields_with_old_and_new()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);

        await r.Products.UpdateAsync(product.Id, Update(name: "Item 2", price: 12.5m), Ct);

        var entries = await r.ListAsync(product.Id);
        Assert.Equal(2, entries.Count);
        var updated = entries.Single(e => e.Action == CatalogProductHistoryActions.Updated);
        Assert.Equal(["name", "price"], updated.Changes.Select(c => c.Field).ToArray());
        Assert.Contains(updated.Changes, c => c is { Field: "name", Old: "Item", New: "Item 2" });
        Assert.Contains(updated.Changes, c => c is { Field: "price", Old: "10.00", New: "12.50" });
    }

    [Fact]
    public async Task Update_without_changes_writes_no_entry()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);

        await r.Products.UpdateAsync(product.Id, Update(), Ct);

        Assert.Equal([CatalogProductHistoryActions.Created], (await r.ListAsync(product.Id)).Select(e => e.Action).ToArray());
    }

    [Fact]
    public async Task Activate_and_deactivate_each_write_one_entry_and_noops_write_none()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);

        await r.Products.DeactivateAsync(product.Id, Ct);
        await r.Products.DeactivateAsync(product.Id, Ct);
        await r.Products.ActivateAsync(product.Id, Ct);
        await r.Products.ActivateAsync(product.Id, Ct);

        Assert.Equal(
            [CatalogProductHistoryActions.Activated, CatalogProductHistoryActions.Created, CatalogProductHistoryActions.Deactivated],
            (await r.ListAsync(product.Id)).Select(e => e.Action).Order(StringComparer.Ordinal).ToArray());
        var deactivated = (await r.ListAsync(product.Id)).Single(e => e.Action == CatalogProductHistoryActions.Deactivated);
        Assert.Contains(deactivated.Changes, c => c is { Field: "isActive", Old: "true", New: "false" });
    }

    [Fact]
    public async Task Taxonomy_change_writes_an_entry_by_name_and_unchanged_taxonomy_writes_none()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);
        var category = new TaxonomyCategory { TenantId = r.H.Tenant.Id, Name = "Quadras", Slug = "quadras" };
        r.H.Db.TaxonomyCategories.Add(category);
        await r.H.Db.SaveChangesAsync();
        var withCategory = new TaxonomyAssignmentRequest { CategoryIds = [category.Id], TagIds = [] };

        await r.Products.UpdateAsync(product.Id, Update(taxonomy: withCategory), Ct);
        await r.Products.UpdateAsync(product.Id, Update(taxonomy: withCategory), Ct);
        await r.Products.UpdateAsync(
            product.Id, Update(taxonomy: new TaxonomyAssignmentRequest { CategoryIds = [], TagIds = [] }), Ct);

        var taxonomy = (await r.ListAsync(product.Id)).Where(e => e.Action == CatalogProductHistoryActions.TaxonomyChanged).ToList();
        Assert.Equal(2, taxonomy.Count);
        Assert.Contains(taxonomy, e => e.Changes.Any(c => c is { Field: "categories", Old: "Quadras", New: null }));
        Assert.Contains(taxonomy, e => e.Changes.Any(c => c is { Field: "categories", Old: null, New: "Quadras" }));
        Assert.DoesNotContain(await r.ListAsync(product.Id), e => e.Action == CatalogProductHistoryActions.Updated);
    }

    [Fact]
    public async Task File_added_and_removed_write_entries_with_the_file_name()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);
        await using var stream = new MemoryStream("%PDF-1.4 dummy"u8.ToArray());

        var file = await r.Products.AddFileAsync(
            product.Id, "tech.pdf", "application/pdf", stream, CatalogFileVisibility.InternalB2B, Ct);
        await r.Products.DeleteFileAsync(product.Id, file.Id, Ct);

        var entries = await r.ListAsync(product.Id);
        var added = entries.Single(e => e.Action == CatalogProductHistoryActions.FileAdded);
        var removed = entries.Single(e => e.Action == CatalogProductHistoryActions.FileRemoved);
        Assert.Contains(added.Changes, c => c is { Field: "file", Old: null, New: "tech.pdf" });
        Assert.Contains(removed.Changes, c => c is { Field: "file", Old: "tech.pdf", New: null });
    }

    [Fact]
    public async Task Resource_requirements_change_writes_an_entry_and_an_identical_replace_writes_none()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);
        var rentalId = await SeedRentalAssetAsync(r.H);
        var requirements = new CatalogProductResourceRequirementService(r.H.Db, r.H.TenantProvider, r.History);
        ReplaceCatalogProductResourceRequirementsRequest Replace(int version) => new()
        {
            ExpectedVersion = version,
            Requirements =
            [
                new CatalogProductResourceRequirementDto
                {
                    RentalAssetId = rentalId,
                    ResourceQuantity = 1,
                    DurationMinutesPerUnit = 45,
                    PrepMinutes = 5,
                    CleanupMinutes = 10,
                },
            ],
        };

        await requirements.ReplaceAsync(product.Id, Replace(0), Ct);
        await requirements.ReplaceAsync(product.Id, Replace(1), Ct);

        var entry = Assert.Single(
            await r.ListAsync(product.Id), e => e.Action == CatalogProductHistoryActions.RequirementsChanged);
        var change = Assert.Single(entry.Changes);
        Assert.Equal("resourceRequirements", change.Field);
        Assert.Null(change.Old);
        Assert.Equal("Printer x1, 45min, prep 5min, cleanup 10min", change.New);
    }

    [Fact]
    public async Task Access_change_rows_are_kept_listed_with_a_derived_diff_and_stay_the_only_rows_of_the_access_history()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);
        var customer = r.H.AddCustomer("Ana");
        await r.H.Db.SaveChangesAsync();
        var groups = new CustomerGroupService(r.H.Db, r.H.TenantProvider);

        await groups.SetProductAccessAsync(
            product.Id, new UpdateCatalogProductAccessRequest("Restricted", [customer.Id], null), Ct);

        var access = (await r.ListAsync(product.Id)).Single(e => e.Action == CatalogProductHistoryActions.AccessChanged);
        Assert.Contains(access.Changes, c => c is { Field: "visibility", Old: "Public", New: "Restricted" });
        Assert.Contains(access.Changes, c => c is { Field: "accessCustomers", Old: "0", New: "1" });
        var legacy = await groups.ListHistoryAsync("product", product.Id, Ct);
        Assert.Equal(CatalogProductHistoryActions.AccessChanged, Assert.Single(legacy).Action);
    }

    [Fact]
    public async Task Other_tenant_cannot_list_the_history_of_a_product()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);
        Assert.NotNull(await r.History.ListAsync(product.Id, 50, null, Ct));

        r.H.TenantProvider.TenantId = Guid.NewGuid();

        Assert.Null(await r.History.ListAsync(product.Id, 50, null, Ct));
        var controller = new CatalogProductsController(null!, null!);
        var result = await controller.History(product.Id, r.History, Ct);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task History_endpoint_returns_entries_newest_first()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);
        await r.Products.UpdateAsync(product.Id, Update(name: "Item 2"), Ct);

        var controller = new CatalogProductsController(null!, null!);
        var result = await controller.History(product.Id, r.History, Ct);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var entries = Assert.IsAssignableFrom<IReadOnlyList<CatalogProductHistoryEntryResponse>>(ok.Value);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].CreatedAt >= entries[1].CreatedAt);
        Assert.Equal(
            [CatalogProductHistoryActions.Created, CatalogProductHistoryActions.Updated],
            entries.Select(e => e.Action).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Paging_walks_back_with_limit_and_before()
    {
        await using var r = await CreateRigAsync();
        var product = new CatalogProduct { TenantId = r.H.Tenant.Id, Name = "Paged" };
        r.H.Db.CatalogProducts.Add(product);
        await r.H.Db.SaveChangesAsync();
        var start = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 5; i++)
        {
            await r.History.RecordAsync(
                product.Id,
                CatalogProductHistoryActions.Updated,
                [new CatalogProductHistoryChange("name", $"v{i}", $"v{i + 1}")],
                Ct);
        }

        var staged = r.H.Db.CatalogAccessAudit.Local.ToList();
        for (var i = 0; i < staged.Count; i++)
        {
            r.H.Db.Entry(staged[i]).Property(a => a.CreatedAt).CurrentValue = start.AddMinutes(i);
        }

        await r.H.Db.SaveChangesAsync();

        var first = (await r.History.ListAsync(product.Id, 2, null, Ct))!;
        var second = (await r.History.ListAsync(product.Id, 2, first[^1].CreatedAt, Ct))!;
        var third = (await r.History.ListAsync(product.Id, 2, second[^1].CreatedAt, Ct))!;

        Assert.Equal([start.AddMinutes(4), start.AddMinutes(3)], first.Select(e => e.CreatedAt).ToArray());
        Assert.Equal([start.AddMinutes(2), start.AddMinutes(1)], second.Select(e => e.CreatedAt).ToArray());
        Assert.Equal([start], third.Select(e => e.CreatedAt).ToArray());
    }

    [Fact]
    public async Task Product_response_exposes_updated_at_after_a_change()
    {
        await using var r = await CreateRigAsync();
        var product = await CreateProductAsync(r);
        Assert.Null(product.UpdatedAt);

        await r.Products.UpdateAsync(product.Id, Update(name: "Item 2"), Ct);

        var reloaded = await r.Products.GetByIdAsync(product.Id, Ct);
        Assert.NotNull(reloaded!.UpdatedAt);
    }

    private static async Task<Guid> SeedRentalAssetAsync(CatalogHarness h)
    {
        var unit = new Unit(h.Tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = h.Tenant.Id, Name = "Equipamentos" };
        var family = new AssetFamily
        {
            Key = $"equip-{Guid.NewGuid():N}"[..32],
            Label = "Equipment",
            FieldSchemaJson = "{}",
        };
        var asset = new Asset
        {
            TenantId = h.Tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Printer",
            Tag = "P1",
            Status = AssetStatus.Active,
            IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = h.Tenant.Id,
            AssetId = asset.Id,
            Type = RentalAssetType.Good,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours,
        };
        h.Db.Units.Add(unit);
        h.Db.AssetCategories.Add(category);
        h.Db.AssetFamilies.Add(family);
        h.Db.Assets.Add(asset);
        h.Db.RentalAssets.Add(rental);
        await h.Db.SaveChangesAsync();
        return rental.Id;
    }
}
