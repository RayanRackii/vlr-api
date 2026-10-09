namespace Platform.Api.Modules.Rentals.Services;

public interface ITeacherLessonScope
{
    bool Allows(Guid tenantId, Guid rentalAssetId);
}
