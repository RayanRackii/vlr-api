using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Api.Tests.Infrastructure;
using Platform.Api.Tests.Fakes;

namespace Platform.Api.Tests.Rentals;

public sealed class PolicyResolverTests
{
    [Theory]
    [InlineData(null, null, null, null, null, null, null, null)]
    [InlineData(null, null, 7, null, null, 24, 7, 24)]
    [InlineData(null, 14, 7, null, 48, 24, 14, 48)]
    [InlineData(0, 14, 7, null, 48, 24, 0, 48)]
    [InlineData(null, 14, 7, 0, 48, 24, 14, 0)]
    [InlineData(null, null, 7, null, 48, 24, 7, 48)]
    [InlineData(0, 14, 7, 0, 48, 24, 0, 0)]
    [InlineData(30, 14, 7, 72, 48, 24, 30, 72)]
    public void Resource_overrides_category_then_tenant_per_field_and_missing_layers_stay_unlimited(
        int? resourceDays, int? categoryDays, int? tenantDays,
        int? resourceCutoff, int? categoryCutoff, int? tenantCutoff,
        int? expectedDays, int? expectedCutoff)
    {
        var tenant = new BookingPolicy
        {
            TenantId = Guid.NewGuid(), MaxAdvanceBookingDays = tenantDays,
            CancelCutoffHours = tenantCutoff, MaxActiveReservationsPerCustomerPerDay = 2,
            CancellationPolicyText = "Return clean.",
        };
        var resource = new RentalAsset
        {
            TenantId = tenant.TenantId, AssetId = Guid.NewGuid(),
            Type = RentalAssetType.Location, TotalQuantity = 1, IsActive = true,
            RequiresDeposit = false, SchedulePolicy = SchedulePolicy.SlotGrid,
            MaxAdvanceBookingDays = resourceDays, CancelCutoffHours = resourceCutoff,
        };

        var category = new BookingPolicy
        {
            TenantId = tenant.TenantId, AssetCategoryId = Guid.NewGuid(),
            MaxAdvanceBookingDays = categoryDays, CancelCutoffHours = categoryCutoff,
            MaxActiveReservationsPerCustomerPerDay = 99, CancellationPolicyText = "Ignored",
        };
        var effective = PolicyResolver.Resolve(resource, category, tenant);

        Assert.Equal(expectedDays, effective.MaxAdvanceBookingDays);
        Assert.Equal(expectedCutoff, effective.CancelCutoffHours);
        Assert.Equal(2, effective.MaxActiveReservationsPerCustomerPerDay);
        Assert.Equal("Return clean.", effective.CancellationPolicyText);
        Assert.Null(PolicyResolver.Resolve(null, null, null).MaxAdvanceBookingDays);
        Assert.Null(PolicyResolver.Resolve(null, null, null).CancelCutoffHours);
    }
    [Fact]
    public async Task Tenant_resolution_ignores_other_tenants_and_typed_policies()
    {
        var tenant = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var db = InMemoryAppDb.Create(tenant);
        db.BookingPolicies.AddRange(
            new BookingPolicy { TenantId = Guid.NewGuid(), CancelCutoffHours = 72, MaxAdvanceBookingDays = 90 },
            new BookingPolicy { TenantId = tenant.TenantId.Value, AssetCategoryId = Guid.NewGuid(),
                CancelCutoffHours = 96, MaxAdvanceBookingDays = 60 },
            new BookingPolicy { TenantId = tenant.TenantId.Value, ResourceType = RentalAssetType.Location,
                CancelCutoffHours = 48, MaxAdvanceBookingDays = 30 });
        await db.SaveChangesAsync();
        var resolver = new PolicyResolver(db);
        Assert.Null(await resolver.ReadTenantAsync(tenant.TenantId.Value, CancellationToken.None));
        db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = tenant.TenantId.Value, CancelCutoffHours = 24, MaxAdvanceBookingDays = 7,
        });
        await db.SaveChangesAsync();
        var policy = PolicyResolver.Resolve(null, null,
            await resolver.ReadTenantAsync(tenant.TenantId.Value, CancellationToken.None));
        Assert.Equal(24, policy.CancelCutoffHours);
        Assert.Equal(7, policy.MaxAdvanceBookingDays);
    }

}
