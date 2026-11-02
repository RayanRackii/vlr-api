using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.ResourcePlanning;

/// <summary>
/// Unit coverage for the serial interval math (order-equipment-link-codex §3: "workEnd = workStart
/// + durationPerUnit * quantity; occupied = [workStart-prep, workEnd+cleanup)") and the
/// never-round work-window rejection rule, independent of any database.
/// </summary>
public sealed class ResourceWorkWindowValidatorTests
{
    private static readonly TimeZoneInfo Zone = BrazilTimeZone.Resolve();

    [Fact]
    public void Serial_quantity_math_multiplies_duration_by_ordered_quantity()
    {
        var workStart = BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(10, 0));
        const int durationPerUnit = 30;
        const int quantity = 3;
        const int prep = 10;
        const int cleanup = 5;

        var workEnd = workStart + TimeSpan.FromMinutes(durationPerUnit * quantity);
        var occupiedStart = workStart - TimeSpan.FromMinutes(prep);
        var occupiedEnd = workEnd + TimeSpan.FromMinutes(cleanup);

        Assert.Equal(workStart.AddMinutes(90), workEnd);
        Assert.Equal(workStart.AddMinutes(-10), occupiedStart);
        Assert.Equal(workEnd.AddMinutes(5), occupiedEnd);
        Assert.Equal(TimeSpan.FromMinutes(105), occupiedEnd - occupiedStart);
    }

    [Fact]
    public void Interval_within_open_hours_and_matching_allowed_duration_is_accepted()
    {
        var rental = OpenHoursRental(open: new TimeOnly(8, 0), close: new TimeOnly(22, 0), allowedDurations: "60");

        var error = ResourceWorkWindowValidator.Validate(
            rental,
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(10, 0)),
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(11, 0)),
            Zone,
            "Impressora 3D");

        Assert.Null(error);
    }

    [Fact]
    public void Interval_outside_open_hours_is_rejected_with_unsupported_work_window_never_rounded()
    {
        var rental = OpenHoursRental(open: new TimeOnly(8, 0), close: new TimeOnly(22, 0), allowedDurations: "60");

        // Starts before opening time — must be rejected outright, never silently clamped to 08:00.
        var error = ResourceWorkWindowValidator.Validate(
            rental,
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(7, 0)),
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(8, 0)),
            Zone,
            "Impressora 3D");

        Assert.NotNull(error);
    }

    [Fact]
    public void Duration_not_in_allowed_list_is_rejected_never_rounded_to_nearest_allowed_value()
    {
        var rental = OpenHoursRental(open: new TimeOnly(8, 0), close: new TimeOnly(22, 0), allowedDurations: "60,120");

        // 90 minutes is between the two allowed durations — must be rejected, not rounded to 60 or 120.
        var error = ResourceWorkWindowValidator.Validate(
            rental,
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(10, 0)),
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(11, 30)),
            Zone,
            "Impressora 3D");

        Assert.NotNull(error);
        Assert.Equal(WorkWindowValidation.UnsupportedWorkWindowCode, ResourceWorkWindowValidator.UnsupportedWorkWindowCode);
    }

    [Fact]
    public void Interval_crossing_midnight_is_rejected_phase_0_is_one_unit_per_day()
    {
        var rental = OpenHoursRental(open: new TimeOnly(0, 0), close: new TimeOnly(23, 59), allowedDurations: "60");

        var error = ResourceWorkWindowValidator.Validate(
            rental,
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(23, 30)),
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 2), new TimeOnly(0, 30)),
            Zone,
            "Impressora 3D");

        Assert.NotNull(error);
    }

    [Fact]
    public void Non_positive_interval_is_rejected()
    {
        var rental = OpenHoursRental(open: new TimeOnly(8, 0), close: new TimeOnly(22, 0), allowedDurations: "60");
        var instant = BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 1), new TimeOnly(10, 0));

        var error = ResourceWorkWindowValidator.Validate(rental, instant, instant, Zone, "Impressora 3D");

        Assert.NotNull(error);
    }

    private static RentalAsset OpenHoursRental(TimeOnly open, TimeOnly close, string allowedDurations) =>
        new()
        {
            TenantId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            Type = RentalAssetType.Location,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours,
            OpenTime = open,
            CloseTime = close,
            AllowedDurationMinutes = allowedDurations,
            QueueEnabled = false,
        };
}
