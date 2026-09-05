using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Admin;

public sealed class AdminTenantReservedSubdomainTests
{
    private const string ReservedMessage = "This subdomain is reserved and cannot be used.";

    [Fact]
    public async Task Create_api_throws_reserved()
    {
        await using var harness = await Harness.CreateAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.CreateAsync(CreateRequest("api"), CancellationToken.None));

        Assert.Equal(ReservedMessage, ex.Message);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("www")]
    [InlineData("ADMIN")]
    [InlineData("Admin")]
    public async Task Create_reserved_slug_throws_after_normalize(string subdomain)
    {
        await using var harness = await Harness.CreateAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.CreateAsync(CreateRequest(subdomain), CancellationToken.None));

        Assert.Equal(ReservedMessage, ex.Message);
    }

    [Theory]
    [InlineData("ficc")]
    [InlineData("clube-x")]
    [InlineData("minha-empresa")]
    public async Task Create_valid_slug_succeeds(string slug)
    {
        await using var harness = await Harness.CreateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await harness.Service.CreateAsync(
            CreateRequest($"{slug}-{suffix}", legalName: slug),
            CancellationToken.None);

        Assert.Equal($"{slug}-{suffix}", created.Subdomain);
    }

    [Fact]
    public async Task Update_normal_tenant_to_admin_throws_reserved()
    {
        await using var harness = await Harness.CreateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await harness.Service.CreateAsync(
            CreateRequest($"club-{suffix}"),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Service.UpdateAsync(
                created.Id,
                UpdateRequest(created, subdomain: "admin"),
                CancellationToken.None));

        Assert.Equal(ReservedMessage, ex.Message);
    }

    [Fact]
    public async Task Update_normal_tenant_to_another_valid_slug_succeeds()
    {
        await using var harness = await Harness.CreateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await harness.Service.CreateAsync(
            CreateRequest($"club-{suffix}"),
            CancellationToken.None);

        var nextSlug = $"arena-{suffix}";
        var updated = await harness.Service.UpdateAsync(
            created.Id,
            UpdateRequest(created, subdomain: nextSlug),
            CancellationToken.None);

        Assert.Equal(nextSlug, updated.Subdomain);
        Assert.Equal(created.LegalName, updated.LegalName);
    }

    [Fact]
    public async Task Update_grandfathered_reserved_slug_can_change_legal_name()
    {
        await using var harness = await Harness.CreateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant(
            "Mailbox Club",
            $"99{suffix}000191",
            subdomain: "mail");
        harness.Db.Tenants.Add(tenant);
        await harness.Db.SaveChangesAsync();

        var updated = await harness.Service.UpdateAsync(
            tenant.Id,
            new UpdateTenantRequestDto
            {
                LegalName = "Mailbox Club Ltda",
                TaxId = tenant.TaxId,
                Subdomain = "mail",
                ActiveModules = [PlatformModules.Catalog],
                AssetFamilyKeys = [AssetFamilyKeys.Generic],
            },
            CancellationToken.None);

        Assert.Equal("mail", updated.Subdomain);
        Assert.Equal("Mailbox Club Ltda", updated.LegalName);
    }

    [Fact]
    public async Task Update_unrelated_fields_on_normal_tenant_keep_subdomain()
    {
        await using var harness = await Harness.CreateAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await harness.Service.CreateAsync(
            CreateRequest($"club-{suffix}", legalName: "Club Original"),
            CancellationToken.None);

        var updated = await harness.Service.UpdateAsync(
            created.Id,
            UpdateRequest(created, legalName: "Club Renamed"),
            CancellationToken.None);

        Assert.Equal(created.Subdomain, updated.Subdomain);
        Assert.Equal("Club Renamed", updated.LegalName);
        Assert.Equal(created.TaxId, updated.TaxId);
    }

    private static CreateTenantRequestDto CreateRequest(string subdomain, string? legalName = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new CreateTenantRequestDto
        {
            LegalName = legalName ?? subdomain,
            TaxId = $"99{suffix}000191",
            Subdomain = subdomain,
            ActiveModules = [PlatformModules.Catalog],
            AssetFamilyKeys = [AssetFamilyKeys.Generic],
        };
    }

    private static UpdateTenantRequestDto UpdateRequest(
        TenantAdminResponseDto tenant,
        string? subdomain = null,
        string? legalName = null) =>
        new()
        {
            LegalName = legalName ?? tenant.LegalName,
            TaxId = tenant.TaxId,
            Subdomain = subdomain ?? tenant.Subdomain ?? throw new InvalidOperationException("Subdomain is required."),
            ActiveModules = tenant.ActiveModules.Select(m => m.ModuleName).ToList(),
            AssetFamilyKeys = tenant.AssetFamilyKeys,
        };

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(AppDbContext db, AdminTenantService service)
        {
            Db = db;
            Service = service;
        }

        public AppDbContext Db { get; }

        public AdminTenantService Service { get; }

        public static async Task<Harness> CreateAsync()
        {
            var tenantProvider = new FakeTenantProvider { TenantId = null };
            var db = InMemoryAppDb.Create(tenantProvider);
            db.AssetFamilies.Add(new AssetFamily
            {
                Key = AssetFamilyKeys.Generic,
                Label = "Genérico",
                FieldSchemaJson = """{"fields":[]}""",
                SortOrder = 4,
                IsActive = true,
            });
            await db.SaveChangesAsync();

            var service = new AdminTenantService(
                db,
                new FakeTenantUserAdminService(),
                new FakePlatformAdminMembershipService(),
                new FakeTenantAccessBootstrapper(),
                new FakeSupabaseAuthAdminClient(),
                Options.Create(new PlatformAdminOptions()),
                NullLogger<AdminTenantService>.Instance);

            return new Harness(db, service);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
