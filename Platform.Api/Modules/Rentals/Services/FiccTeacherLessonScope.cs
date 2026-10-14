namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Reviewed teacher-lesson courts. Production keeps the six SlotGrid locations
/// censused on 2026-10-08. FICC DEV is a different tenant with its own rental
/// asset ids; only Quadra 1 and Quadra 2 are included. Asset ids are not
/// accepted. A new court still needs an explicit reviewed change.
/// </summary>
public sealed class FiccTeacherLessonScope : ITeacherLessonScope
{
    public static readonly Guid TenantId = Guid.Parse("3ae86b53-d43e-42b0-9616-4833d02f7a1e");

    public static readonly Guid DevTenantId = Guid.Parse("b27c9a0a-d585-4626-8c13-1468c5a43519");

    public static readonly Guid DevQuadra1RentalAssetId = Guid.Parse("e82cbc28-0fdc-4d44-a062-2efede0f16b8");

    public static readonly Guid DevQuadra2RentalAssetId = Guid.Parse("e56e4f22-f45e-486d-a80b-a3714d6e3d5e");

    /// <summary>Asset id of DEV Quadra 1. Create must keep rejecting this value.</summary>
    public static readonly Guid DevQuadra1AssetId = Guid.Parse("388931ba-5233-4da9-b49f-d8304e48c89b");

    /// <summary>Asset id of DEV Quadra 2. Create must keep rejecting this value.</summary>
    public static readonly Guid DevQuadra2AssetId = Guid.Parse("2a01d706-2021-43d1-a578-ba0892ca7739");

    private static readonly IReadOnlyDictionary<Guid, HashSet<Guid>> RentalAssetIdsByTenant =
        new Dictionary<Guid, HashSet<Guid>>
        {
            [TenantId] =
            [
                Guid.Parse("98259729-fa57-4b5a-abcf-1d36603fc96e"),
                Guid.Parse("edb91e39-e03d-42f5-b38a-518a5427344b"),
                Guid.Parse("35fa8602-f7c1-4876-9b18-dd1f8e8a546b"),
                Guid.Parse("04721eb7-d6c2-4197-9930-a8874de5ad27"),
                Guid.Parse("4f586a63-a89e-467b-b139-a3125a2ba9ec"),
                Guid.Parse("32b9bd39-9e32-4f25-ad6d-67117c882a3c"),
            ],
            [DevTenantId] =
            [
                DevQuadra1RentalAssetId,
                DevQuadra2RentalAssetId,
            ],
        };

    public bool Allows(Guid tenantId, Guid rentalAssetId) =>
        RentalAssetIdsByTenant.TryGetValue(tenantId, out var rentalAssetIds)
        && rentalAssetIds.Contains(rentalAssetId);
}
