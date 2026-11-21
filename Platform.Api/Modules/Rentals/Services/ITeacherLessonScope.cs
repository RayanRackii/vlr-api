namespace Platform.Api.Modules.Rentals.Services;

/// <summary>Which rental assets of a tenant may have teacher lessons.</summary>
public interface ITeacherLessonScope
{
    Task<bool> AllowsAsync(Guid tenantId, Guid rentalAssetId, CancellationToken cancellationToken);
}
