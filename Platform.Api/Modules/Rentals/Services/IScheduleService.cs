using Platform.Api.Modules.Rentals.Dtos;

namespace Platform.Api.Modules.Rentals.Services;

public interface IScheduleService
{
    Task<IReadOnlyList<ScheduleTemplateResponseDto>> ListTemplatesAsync(
        Guid? rentalAssetId,
        CancellationToken cancellationToken);

    Task<ScheduleTemplateResponseDto> CreateTemplateAsync(
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken);

    Task<ScheduleTemplateResponseDto> UpdateTemplateAsync(
        Guid id,
        UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken);

    Task DeleteTemplateAsync(Guid id, CancellationToken cancellationToken);

    Task<SeedDefaultTemplatesResponseDto> SeedDefaultTemplatesAsync(
        SeedDefaultTemplatesRequestDto request,
        CancellationToken cancellationToken);

    Task<DayScheduleResponseDto> GetDayAsync(
        DateOnly date,
        Guid? rentalAssetId,
        bool customerFacing,
        CancellationToken cancellationToken);

    Task<int> PublishDayAsync(
        PublishDayRequestDto request,
        CancellationToken cancellationToken);

    Task<SlotResponseDto> UpsertSlotAsync(
        UpsertSlotRequestDto request,
        CancellationToken cancellationToken);

    Task CancelSlotAsync(Guid slotId, CancellationToken cancellationToken);

    Task<ReservationResponseDto> BookSlotAsync(
        Guid customerId,
        BookSlotRequestDto request,
        CancellationToken cancellationToken);
}
