namespace Platform.Api.Modules.Rentals.Services;

/// <summary>
/// Fixed pilot scope for teacher lesson overrides at FICC. Expanding this list
/// requires an explicit code review; newly created courts are not enabled implicitly.
/// </summary>
public sealed class FiccTeacherLessonScope : ITeacherLessonScope
{
    public static readonly Guid TenantId = Guid.Parse("3ae86b53-d43e-42b0-9616-4833d02f7a1e");

    private static readonly HashSet<Guid> RentalAssetIds =
    [
        Guid.Parse("98259729-fa57-4b5a-abcf-1d36603fc96e"),
        Guid.Parse("edb91e39-e03d-42f5-b38a-518a5427344b"),
        Guid.Parse("35fa8602-f7c1-4876-9b18-dd1f8e8a546b"),
        Guid.Parse("04721eb7-d6c2-4197-9930-a8874de5ad27"),
        Guid.Parse("4f586a63-a89e-467b-b139-a3125a2ba9ec"),
        Guid.Parse("32b9bd39-9e32-4f25-ad6d-67117c882a3c"),
    ];

    public bool Allows(Guid tenantId, Guid rentalAssetId) =>
        tenantId == TenantId && RentalAssetIds.Contains(rentalAssetId);
}
