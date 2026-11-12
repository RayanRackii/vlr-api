using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Infrastructure;

namespace Platform.Api.Tests.Rentals;

public sealed class ResourceTypeTests
{
    [Fact]
    public async Task List_seeds_quadra_and_cacamba_for_the_tenant()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false);
        var service = new ResourceTypeService(h.Db, h.TenantProvider);

        var list = await service.ListAsync(CancellationToken.None);

        Assert.Equal(["cacamba", "quadra"], list.Select(type => type.Key).Order().ToArray());
        var quadra = list.Single(type => type.Key == "quadra");
        Assert.Equal("Quadra", quadra.Name);
        Assert.Equal("uma", quadra.Article);
        Assert.True(quadra.Seeded);
        Assert.True(quadra.Capabilities.Bookable);
        Assert.False(quadra.Capabilities.Deliverable);
        Assert.True(quadra.Capabilities.Maintenance);
        Assert.Empty(quadra.Fields);
    }

    [Fact]
    public async Task Create_and_update_keep_seeded_keys_and_internal_fields()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false);
        var service = new ResourceTypeService(h.Db, h.TenantProvider);
        var created = await service.CreateAsync(Write("profissional", "Profissional", "uma", internalField: true), CancellationToken.None);
        Assert.False(created.Seeded);
        Assert.Equal("internal", created.Fields.Single().Visibility);
        Assert.False(created.Fields.Single().Filterable);
        Assert.Equal("number", created.Fields.Single().ResponseType);
        Assert.Equal("number", created.Fields.Single().Type);

        var updated = await service.UpdateAsync(created.Id, Write("profissional", "Profissional", "uma", internalField: true, plural: "pessoas"), CancellationToken.None);
        Assert.Equal("pessoas", updated.Plural);
        Assert.Equal(created.Id, (await service.GetAsync(created.Id, CancellationToken.None)).Id);

        var quadra = (await service.ListAsync(CancellationToken.None)).Single(type => type.Key == "quadra");
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(quadra.Id, Write("outro", "Quadra", "uma", internalField: false), CancellationToken.None));
        Assert.Contains("key", error.Message, StringComparison.OrdinalIgnoreCase);

        var ownerId = h.TenantProvider.TenantId;
        h.TenantProvider.TenantId = Guid.NewGuid();
        var other = await service.ListAsync(CancellationToken.None);
        Assert.DoesNotContain(other, type => type.Id == created.Id);
        Assert.Contains(other, type => type.Key == "quadra");
        h.TenantProvider.TenantId = ownerId;

        h.Db.ChangeTracker.Clear();
        var rental = await h.Db.RentalAssets.SingleAsync();
        rental.ResourceTypeId = quadra.Id;
        await h.Db.SaveChangesAsync();
        Assert.Equal(1, (await service.GetAsync(quadra.Id, CancellationToken.None)).ResourceCount);
    }

    [Fact]
    public async Task A_duplicate_key_is_rejected()
    {
        await using var h = await ReservationQueueHarness.CreateAsync(queueEnabled: false);
        var service = new ResourceTypeService(h.Db, h.TenantProvider);
        await service.CreateAsync(Write("sala", "Sala", "uma", internalField: false), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Write("sala", "Sala 2", "uma", internalField: false), CancellationToken.None));
    }

    private static ResourceTypeWriteRequest Write(string key, string name, string article, bool internalField, string? plural = null) =>
        new()
        {
            Key = key,
            Name = name,
            Singular = name.ToLowerInvariant(),
            Plural = plural ?? name.ToLowerInvariant() + "s",
            Article = article,
            Capabilities = new ResourceTypeCapabilitiesDto(true, false, false),
            Fields =
            [
                new ResourceTypeFieldDto
                {
                    Key = "comissao",
                    Name = "Comissão",
                    ResponseType = internalField ? "número" : "text",
                    Visibility = internalField ? "internal" : "public",
                    Filterable = true,
                    AskOnBooking = true,
                    Required = false,
                    Options = [],
                },
            ],
        };
}
