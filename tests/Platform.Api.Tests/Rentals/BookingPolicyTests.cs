using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Rentals.Controllers;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Serialization;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Api.Tests.Rbac;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingPolicyTests
{
    private static readonly DateOnly Day = new(2026, 10, 14);
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);
    private static readonly TimeOnly Noon = new(12, 0);
    private static readonly TimeOnly One = new(13, 0);

    [Fact]
    public async Task Category_policy_lists_rental_categories_replaces_values_and_rejects_other_tenants()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        var asset = await harness.Db.Assets.SingleAsync(a => a.Id == harness.AssetId);
        var categories = await harness.Policies.GetCategoriesAsync(CancellationToken.None);
        var category = Assert.Single(categories, c => c.CategoryId == asset.CategoryId);
        Assert.Null(category.MaxAdvanceBookingDays);
        Assert.Null(category.CancelCutoffHours);

        var saved = await harness.Policies.UpsertCategoryAsync(asset.CategoryId,
            new(14, 48), CancellationToken.None);
        Assert.Equal(14, saved.MaxAdvanceBookingDays);
        Assert.Equal(48, saved.CancelCutoffHours);
        Assert.Null((await harness.Policies.GetAsync(CancellationToken.None)).MaxAdvanceBookingDays);
        var rental = await new RentalAssetService(harness.Db, harness.TenantProvider)
            .GetByAssetIdAsync(asset.Id, CancellationToken.None);
        Assert.Equal(14, rental!.EffectiveMaxAdvanceBookingDays);
        Assert.Equal(48, rental.EffectiveCancelCutoffHours);
        var cleared = await harness.Policies.UpsertCategoryAsync(asset.CategoryId,
            new(null, null), CancellationToken.None);
        Assert.Null(cleared.MaxAdvanceBookingDays);
        Assert.Null(cleared.CancelCutoffHours);
        Assert.Contains(await harness.Policies.GetCategoriesAsync(CancellationToken.None),
            c => c.CategoryId == asset.CategoryId && c.MaxAdvanceBookingDays == null && c.CancelCutoffHours == null);

        var foreignCategory = new AssetCategory { TenantId = Guid.NewGuid(), Name = "Other tenant" };
        harness.Db.AssetCategories.Add(foreignCategory);
        var unusedCategory = new AssetCategory { TenantId = harness.TenantId, Name = "Unused" };
        harness.Db.AssetCategories.Add(unusedCategory);
        await harness.Db.SaveChangesAsync();
        Assert.DoesNotContain(await harness.Policies.GetCategoriesAsync(CancellationToken.None),
            c => c.CategoryId == foreignCategory.Id || c.CategoryId == unusedCategory.Id);
        var controller = new BookingPolicyController(harness.Policies, null!);
        var response = await controller.UpsertCategory(foreignCategory.Id, new(7, 24), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(response.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Category_horizon_applies_to_booking_availability_and_public_day(bool slotBooking)
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        var asset = await harness.Db.Assets.SingleAsync(a => a.Id == harness.AssetId);
        var rentalId = await harness.Db.RentalAssets.Where(r => r.AssetId == asset.Id)
            .Select(r => r.Id).SingleAsync();
        await harness.Policies.UpsertCategoryAsync(asset.CategoryId, new(1, null), CancellationToken.None);
        var requestedDay = Day.AddDays(2);
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() => harness.Reservations.CheckAvailabilityAsync(
            new CheckAvailabilityRequestDto { AssetId = asset.Id, Date = requestedDay, StartTime = Ten, EndTime = Eleven },
            CancellationToken.None));
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() => harness.Schedule.GetDayAsync(
            requestedDay, rentalId, customerFacing: true, CancellationToken.None));
        if (slotBooking)
        {
            var slot = await harness.SeedSlotAsync(requestedDay, Ten, Eleven);
            await Assert.ThrowsAsync<BookingHorizonExceededException>(() => harness.Schedule.BookSlotAsync(
                harness.CustomerId, new BookSlotRequestDto { SlotId = slot, UnitId = harness.UnitId }, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<BookingHorizonExceededException>(() => harness.BookAsync(requestedDay, Ten, Eleven));
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3651, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 8761)]
    public async Task Category_policy_rejects_invalid_values(int days, int cutoff)
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        var asset = await harness.Db.Assets.SingleAsync(a => a.Id == harness.AssetId);
        var controller = new BookingPolicyController(harness.Policies, null!);
        var response = await controller.UpsertCategory(asset.CategoryId, new(days, cutoff), CancellationToken.None);
        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal(400, Assert.IsType<ProblemDetails>(badRequest.Value).Status);
    }

    [Fact]
    public async Task Cancellation_cutoff_round_trips_preserves_omitted_and_clears_null()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        Assert.Null((await harness.Policies.GetAsync(CancellationToken.None)).CancelCutoffHours);
        var saved = await harness.Policies.UpsertAsync(
            DeserializePolicy("{\"cancelCutoffHours\":24}"), CancellationToken.None);
        Assert.Equal(24, saved.CancelCutoffHours);
        var kept = await harness.Policies.UpsertAsync(DeserializePolicy("{}"), CancellationToken.None);
        Assert.Equal(24, kept.CancelCutoffHours);
        var cleared = await harness.Policies.UpsertAsync(
            DeserializePolicy("{\"cancelCutoffHours\":null}"), CancellationToken.None);
        Assert.Null(cleared.CancelCutoffHours);
        Assert.Null((await harness.Policies.GetAsync(CancellationToken.None)).CancelCutoffHours);
    }

    [Fact]
    public async Task Rentable_responses_expose_the_asset_family()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        var asset = await harness.Db.Assets.SingleAsync(a => a.Id == harness.AssetId);
        var service = new RentalAssetService(harness.Db, harness.TenantProvider);

        var single = await service.GetByAssetIdAsync(harness.AssetId, CancellationToken.None);
        var listed = (await service.ListRentableAsync(CancellationToken.None)).Single(r => r.AssetId == harness.AssetId);

        Assert.NotEqual(Guid.Empty, asset.FamilyId);
        Assert.Equal(asset.FamilyId, single!.FamilyId);
        Assert.Equal(asset.FamilyId, listed.FamilyId);
    }

    [Fact]
    public async Task Resource_cutoff_update_preserves_omitted_and_clears_null()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        var rental = await harness.Db.RentalAssets.SingleAsync(r => r.AssetId == harness.AssetId);
        var service = new RentalAssetService(harness.Db, harness.TenantProvider);
        var request = new UpdateRentalSchedulePolicyRequestDto
        {
            SchedulePolicy = SchedulePolicy.OpenHours, OpenTime = new TimeOnly(8, 0), CloseTime = new TimeOnly(22, 0),
            CancelCutoffHours = 48,
        };
        Assert.Equal(48, (await service.UpdateSchedulePolicyAsync(rental.Id, request, CancellationToken.None)).CancelCutoffHours);
        var omitted = request with { CancelCutoffHours = default };
        Assert.Equal(48, (await service.UpdateSchedulePolicyAsync(rental.Id, omitted, CancellationToken.None)).CancelCutoffHours);
        var cleared = request with { CancelCutoffHours = JsonOptional<int>.Specified(null) };
        Assert.Null((await service.UpdateSchedulePolicyAsync(rental.Id, cleared, CancellationToken.None)).CancelCutoffHours);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Both_booking_paths_return_the_effective_cancellation_deadline(bool slotBooking, bool categoryPolicy)
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        if (categoryPolicy)
        {
            var asset = await harness.Db.Assets.SingleAsync(a => a.Id == harness.AssetId);
            await harness.Policies.UpsertCategoryAsync(asset.CategoryId, new(null, 24), CancellationToken.None);
        }
        else
        {
            await harness.Policies.UpsertAsync(new UpsertBookingPolicyRequestDto { CancelCutoffHours = 24 }, CancellationToken.None);
        }
        ReservationResponseDto response;
        if (slotBooking)
        {
            var slot = await harness.SeedSlotAsync(Day, Ten, Eleven);
            response = await harness.Schedule.BookSlotAsync(harness.CustomerId,
                new BookSlotRequestDto { SlotId = slot, UnitId = harness.UnitId }, CancellationToken.None);
        }
        else
        {
            response = await harness.BookAsync(Day, Ten, Eleven);
        }
        var expected = new DateTimeOffset(2026, 10, 13, 13, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, response.CancelDeadlineUtc);
        var confirmed = await harness.Reservations.ConfirmAsync(response.Id, CancellationToken.None);
        Assert.Equal(expected, confirmed.CancelDeadlineUtc);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8761)]
    public async Task Resource_cutoff_rejects_out_of_range_values(int hours)
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        var rental = await harness.Db.RentalAssets.SingleAsync(r => r.AssetId == harness.AssetId);
        var service = new RentalAssetService(harness.Db, harness.TenantProvider);
        var request = new UpdateRentalSchedulePolicyRequestDto
        {
            SchedulePolicy = SchedulePolicy.OpenHours, OpenTime = new TimeOnly(8, 0), CloseTime = new TimeOnly(22, 0),
            CancelCutoffHours = hours,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateSchedulePolicyAsync(rental.Id, request, CancellationToken.None));
        Assert.Null((await service.GetByAssetIdAsync(harness.AssetId, CancellationToken.None))!.CancelCutoffHours);
    }

    [Fact]
    public async Task Missing_policy_is_unlimited_and_stores_sanitized_plain_text()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));

        var before = await harness.Policies.GetAsync(CancellationToken.None);
        Assert.Null(before.CancellationPolicyText);
        Assert.Null(before.MaxActiveReservationsPerCustomerPerDay);
        Assert.Null(before.MaxAdvanceBookingDays);

        await harness.BookAsync(Day, Ten, Eleven);
        await harness.BookAsync(Day, Eleven, Noon);
        await harness.BookAsync(Day, Noon, One);
        Assert.Equal(3, await harness.Db.Reservations.CountAsync());

        var saved = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto
            {
                CancellationPolicyText = "  <b>Bring it back</b> clean.\n<script>alert(1)</script>  ",
                MaxActiveReservationsPerCustomerPerDay = null,
            },
            CancellationToken.None);

        Assert.Equal("Bring it back clean.\nalert(1)", saved.CancellationPolicyText);
        Assert.Null(saved.MaxActiveReservationsPerCustomerPerDay);
        Assert.Null(saved.MaxAdvanceBookingDays);
        Assert.DoesNotContain("court", saved.CancellationPolicyText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quadra", DailyBookingLimitReachedException.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_rejects_overlong_text_and_out_of_range_limits()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));

        var tooLong = new string('a', BookingPolicy.CancellationPolicyMaxLength + 1);
        var length = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Policies.UpsertAsync(
                new UpsertBookingPolicyRequestDto { CancellationPolicyText = tooLong },
                CancellationToken.None));
        Assert.Contains("2000", length.Message, StringComparison.Ordinal);

        var zero = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Policies.UpsertAsync(
                new UpsertBookingPolicyRequestDto { MaxActiveReservationsPerCustomerPerDay = 0 },
                CancellationToken.None));
        Assert.Contains("1 to 100", zero.Message, StringComparison.Ordinal);

        Assert.Empty(await harness.Db.BookingPolicies.ToListAsync());
    }

    [Fact]
    public async Task Encoded_markup_is_stored_as_plain_text_and_blank_clears_the_note()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));

        var saved = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto
            {
                CancellationPolicyText = "&lt;b&gt;Keep the resource&lt;/b&gt;",
                MaxActiveReservationsPerCustomerPerDay = 2,
            },
            CancellationToken.None);
        Assert.Equal("Keep the resource", saved.CancellationPolicyText);

        var cleared = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto
            {
                CancellationPolicyText = "   ",
                MaxActiveReservationsPerCustomerPerDay = 2,
            },
            CancellationToken.None);
        Assert.Null(cleared.CancellationPolicyText);
        Assert.Equal(2, cleared.MaxActiveReservationsPerCustomerPerDay);
    }

    [Fact]
    public async Task Tenant_default_is_isolated_and_a_typed_row_is_not_the_policy()
    {
        var database = Guid.NewGuid().ToString("N");
        await using var left = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0), database, "11111111000191");
        await left.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto
            {
                CancellationPolicyText = "Tenant A note",
                MaxActiveReservationsPerCustomerPerDay = 2,
            },
            CancellationToken.None);
        left.Db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = left.TenantId,
            ResourceType = RentalAssetType.Location,
            CancellationPolicyText = "typed override",
            MaxActiveReservationsPerCustomerPerDay = 9,
        });
        await left.Db.SaveChangesAsync();

        await using var right = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0), database, "22222222000191");
        var other = await right.Policies.GetAsync(CancellationToken.None);
        Assert.Null(other.CancellationPolicyText);
        Assert.Null(other.MaxActiveReservationsPerCustomerPerDay);

        var own = await left.Policies.GetAsync(CancellationToken.None);
        Assert.Equal("Tenant A note", own.CancellationPolicyText);
        Assert.Equal(2, own.MaxActiveReservationsPerCustomerPerDay);
    }

    [Fact]
    public async Task Daily_limit_blocks_the_next_self_booking_and_ignores_canceled()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        await harness.SetLimitAsync(1);

        var first = await harness.BookAsync(Day, Ten, Eleven);
        Assert.Equal(ReservationStatus.Confirmed, first.Status);

        var blocked = await Assert.ThrowsAsync<DailyBookingLimitReachedException>(() =>
            harness.BookAsync(Day, Eleven, Noon));
        Assert.Equal("DAILY_BOOKING_LIMIT_REACHED", DailyBookingLimitReachedException.Code);
        Assert.Equal(1, blocked.Limit);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync());

        await harness.Reservations.CancelAsync(first.Id, CancellationToken.None);
        var again = await harness.BookAsync(Day, Eleven, Noon);
        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync(reservation =>
            reservation.Status != ReservationStatus.Canceled));
        Assert.Equal(1, await harness.Db.Reservations.CountAsync(reservation =>
            reservation.Status == ReservationStatus.Canceled));
    }

    [Fact]
    public async Task Pending_and_confirmed_count_completed_does_not()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        await harness.SetLimitAsync(1);

        var pendingHarness = await harness.WithDepositRequiredAsync();
        var pending = await pendingHarness.BookAsync(Day, Ten, Eleven);
        Assert.Equal(ReservationStatus.PendingDeposit, pending.Status);
        await Assert.ThrowsAsync<DailyBookingLimitReachedException>(() =>
            pendingHarness.BookAsync(Day, Eleven, Noon));

        var completed = await harness.Db.Reservations.SingleAsync(reservation => reservation.Id == pending.Id);
        completed.Status = ReservationStatus.Completed;
        var rental = await harness.Db.RentalAssets.SingleAsync(item => item.AssetId == harness.AssetId);
        rental.RequiresDeposit = false;
        await harness.Db.SaveChangesAsync();

        var afterComplete = await harness.BookAsync(Day, Noon, One);
        Assert.Equal(ReservationStatus.Confirmed, afterComplete.Status);
        await Assert.ThrowsAsync<DailyBookingLimitReachedException>(() =>
            harness.BookAsync(Day, new TimeOnly(14, 0), new TimeOnly(15, 0)));

        var other = await harness.BookAsOtherCustomerAsync(Day, Eleven, Noon);
        Assert.Equal(harness.OtherCustomerId, other.CustomerId);
    }

    [Fact]
    public async Task Put_replaces_both_fields_and_an_omitted_value_clears_it()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));

        var saved = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto
            {
                CancellationPolicyText = "Bring the resource back clean.",
                MaxActiveReservationsPerCustomerPerDay = 3,
            },
            CancellationToken.None);
        Assert.Equal("Bring the resource back clean.", saved.CancellationPolicyText);
        Assert.Equal(3, saved.MaxActiveReservationsPerCustomerPerDay);
        Assert.Null(saved.MaxAdvanceBookingDays);

        var clearedText = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { MaxActiveReservationsPerCustomerPerDay = 4 },
            CancellationToken.None);
        Assert.Null(clearedText.CancellationPolicyText);
        Assert.Equal(4, clearedText.MaxActiveReservationsPerCustomerPerDay);
        Assert.Null(clearedText.MaxAdvanceBookingDays);

        var clearedLimit = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { CancellationPolicyText = "Same note" },
            CancellationToken.None);
        Assert.Equal("Same note", clearedLimit.CancellationPolicyText);
        Assert.Null(clearedLimit.MaxActiveReservationsPerCustomerPerDay);
        Assert.Null(clearedLimit.MaxAdvanceBookingDays);
    }

    [Fact]
    public async Task Tenant_without_a_policy_row_stays_unlimited()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));

        var read = await harness.Policies.GetAsync(CancellationToken.None);
        Assert.Null(read.MaxAdvanceBookingDays);
        Assert.Empty(await harness.Db.BookingPolicies.IgnoreQueryFilters().ToListAsync());

        var booked = await harness.BookAsync(Day.AddDays(8), Ten, Eleven);
        Assert.NotEqual(Guid.Empty, booked.Id);

        var created = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto
            {
                CancellationPolicyText = "Resource note",
            },
            CancellationToken.None);
        Assert.Null(created.MaxAdvanceBookingDays);
        var stored = await harness.Db.BookingPolicies.SingleAsync();
        Assert.Null(stored.MaxAdvanceBookingDays);
        Assert.Equal("Resource note", stored.CancellationPolicyText);
    }

    [Fact]
    public async Task Existing_policy_row_is_not_rewritten_to_seven_days()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        harness.Db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = harness.TenantId,
            ResourceType = null,
            MaxAdvanceBookingDays = null,
        });
        await harness.Db.SaveChangesAsync();

        var unlimited = await harness.Policies.GetAsync(CancellationToken.None);
        Assert.Null(unlimited.MaxAdvanceBookingDays);

        var row = await harness.Db.BookingPolicies.SingleAsync();
        row.MaxAdvanceBookingDays = 365;
        await harness.Db.SaveChangesAsync();

        var kept = await harness.Policies.GetAsync(CancellationToken.None);
        Assert.Equal(365, kept.MaxAdvanceBookingDays);

        var omitted = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { CancellationPolicyText = "Keep the resource note" },
            CancellationToken.None);
        Assert.Equal(365, omitted.MaxAdvanceBookingDays);
        Assert.Equal(365, (await harness.Db.BookingPolicies.SingleAsync()).MaxAdvanceBookingDays);
    }

    [Fact]
    public async Task Explicit_null_clears_a_seven_day_horizon_to_unlimited()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        harness.Db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = harness.TenantId,
            ResourceType = null,
            MaxAdvanceBookingDays = 7,
        });
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            harness.BookAsync(Day.AddDays(8), Ten, Eleven));

        var cleared = await harness.Policies.UpsertAsync(
            DeserializePolicy("""{"maxAdvanceBookingDays":null}"""),
            CancellationToken.None);
        Assert.Null(cleared.MaxAdvanceBookingDays);
        Assert.Null((await harness.Db.BookingPolicies.SingleAsync()).MaxAdvanceBookingDays);

        var far = await harness.BookAsync(new DateOnly(2099, 12, 31), Ten, Eleven);
        Assert.NotEqual(Guid.Empty, far.Id);
        Assert.DoesNotContain("court", BookingHorizonExceededException.DefaultMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quadra", BookingHorizonExceededException.DefaultMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Omitted_horizon_keeps_the_stored_value_and_explicit_null_clears_it()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));

        var stored = await harness.Policies.UpsertAsync(
            DeserializePolicy("""{"cancellationPolicyText":"Note","maxAdvanceBookingDays":365}"""),
            CancellationToken.None);
        Assert.Equal(365, stored.MaxAdvanceBookingDays);

        var omitted = DeserializePolicy("""{"cancellationPolicyText":"Still here"}""");
        Assert.False(omitted.MaxAdvanceBookingDays.IsSpecified);
        var kept = await harness.Policies.UpsertAsync(omitted, CancellationToken.None);
        Assert.Equal("Still here", kept.CancellationPolicyText);
        Assert.Equal(365, kept.MaxAdvanceBookingDays);

        var updated = DeserializePolicy("""{"maxAdvanceBookingDays":30}""");
        Assert.True(updated.MaxAdvanceBookingDays.IsSpecified);
        Assert.Equal(30, updated.MaxAdvanceBookingDays.Value);
        var changed = await harness.Policies.UpsertAsync(updated, CancellationToken.None);
        Assert.Null(changed.CancellationPolicyText);
        Assert.Equal(30, changed.MaxAdvanceBookingDays);

        var explicitNull = DeserializePolicy("""{"cancellationPolicyText":"Note","maxAdvanceBookingDays":null}""");
        Assert.True(explicitNull.MaxAdvanceBookingDays.IsSpecified);
        Assert.Null(explicitNull.MaxAdvanceBookingDays.Value);
        var cleared = await harness.Policies.UpsertAsync(explicitNull, CancellationToken.None);
        Assert.Null(cleared.MaxAdvanceBookingDays);

        var primary = await harness.Db.RentalAssets.SingleAsync(rental => rental.AssetId == harness.AssetId);
        var assets = new RentalAssetService(harness.Db, harness.TenantProvider);
        var withOverride = await assets.SetMaxAdvanceBookingDaysAsync(
            primary.Id,
            DeserializeRentable(12).MaxAdvanceBookingDays,
            CancellationToken.None);
        Assert.Equal(12, withOverride.MaxAdvanceBookingDays);
        Assert.Equal(12, withOverride.EffectiveMaxAdvanceBookingDays);

        var omittedOverride = DeserializeRentable(null);
        Assert.False(omittedOverride.MaxAdvanceBookingDays.IsSpecified);
        var keptOverride = await assets.SetMaxAdvanceBookingDaysAsync(
            primary.Id,
            omittedOverride.MaxAdvanceBookingDays,
            CancellationToken.None);
        Assert.Equal(12, keptOverride.MaxAdvanceBookingDays);

        var changedOverride = await assets.SetMaxAdvanceBookingDaysAsync(
            primary.Id,
            DeserializeRentable(4).MaxAdvanceBookingDays,
            CancellationToken.None);
        Assert.Equal(4, changedOverride.MaxAdvanceBookingDays);

        var clearedJson = DeserializeRentable(null, includeHorizon: true);
        Assert.True(clearedJson.MaxAdvanceBookingDays.IsSpecified);
        Assert.Null(clearedJson.MaxAdvanceBookingDays.Value);
        var clearedOverride = await assets.SetMaxAdvanceBookingDaysAsync(
            primary.Id,
            clearedJson.MaxAdvanceBookingDays,
            CancellationToken.None);
        Assert.Null(clearedOverride.MaxAdvanceBookingDays);
        Assert.Null(clearedOverride.EffectiveMaxAdvanceBookingDays);
    }

    [Fact]
    public void Missing_tenant_timezone_falls_back_to_America_Sao_Paulo()
    {
        var fallback = TenantBookingClock.Resolve(null);
        Assert.Equal(BrazilTimeZone.Resolve().Id, fallback.Id);
        Assert.Equal(fallback.Id, TenantBookingClock.Resolve("   ").Id);
        Assert.Equal(fallback.Id, TenantBookingClock.Resolve("Not/AZone").Id);

        var utc = TenantBookingClock.Resolve("UTC");
        var (utcStart, utcEnd) = TenantBookingClock.CivilDay(Day, utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), utcStart);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), utcEnd);

        var brazilEvening = BrazilTimeZone.AtLocal(Day, new TimeOnly(22, 0));
        Assert.Equal(
            BrazilTimeZone.AtLocal(Day, new TimeOnly(10, 0)),
            TenantBookingClock.AtLocal(Day, new TimeOnly(10, 0), fallback));
        var manaus = TenantBookingClock.Resolve("America/Manaus");
        Assert.NotEqual(
            BrazilTimeZone.AtLocal(Day, new TimeOnly(10, 0)),
            TenantBookingClock.AtLocal(Day, new TimeOnly(10, 0), manaus));
        var (brazilStart, brazilEnd) = TenantBookingClock.CivilDay(Day, fallback);
        Assert.InRange(brazilEvening, brazilStart, brazilEnd.AddTicks(-1));
        Assert.True(brazilEvening >= utcEnd);
    }

    [Fact]
    public async Task Civil_day_boundary_uses_America_Sao_Paulo_not_the_utc_date()
    {
        var clock = BrazilTimeZone.AtLocal(Day, new TimeOnly(12, 0));
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, clock);
        var tenant = await harness.Db.Tenants.SingleAsync();
        Assert.Null(await TenantBookingClock.ReadConfiguredTimeZoneIdAsync(harness.Db, tenant.Id, default));
        await harness.SetLimitAsync(1);

        var evening = await harness.BookAsync(Day, new TimeOnly(22, 0), new TimeOnly(23, 0));
        Assert.Equal(new DateOnly(2026, 10, 15), DateOnly.FromDateTime(evening.StartDateTime.UtcDateTime));
        Assert.Equal(Day, BrazilTimeZone.GetCivilDate(evening.StartDateTime));

        var nextDay = await harness.BookAsync(Day.AddDays(1), new TimeOnly(0, 0), new TimeOnly(1, 0));
        Assert.Equal(Day.AddDays(1), BrazilTimeZone.GetCivilDate(nextDay.StartDateTime));
        Assert.Equal(new DateOnly(2026, 10, 15), DateOnly.FromDateTime(nextDay.StartDateTime.UtcDateTime));

        await Assert.ThrowsAsync<DailyBookingLimitReachedException>(() =>
            harness.BookAsync(Day, new TimeOnly(18, 0), new TimeOnly(19, 0)));
    }

    [Fact]
    public async Task Another_tenants_reservation_does_not_consume_the_cap_when_the_filter_is_off()
    {
        var database = Guid.NewGuid().ToString("N");
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var provider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(provider, database);
        db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = tenantA,
            ResourceType = null,
            MaxActiveReservationsPerCustomerPerDay = 1,
        });
        db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = tenantA,
            ResourceType = RentalAssetType.Good,
            MaxActiveReservationsPerCustomerPerDay = 1,
        });
        db.Reservations.Add(new Reservation
        {
            TenantId = tenantB,
            UnitId = Guid.NewGuid(),
            CustomerId = customerId,
            CustomerName = "Other",
            CustomerWhatsApp = "",
            StartDateTime = BrazilTimeZone.AtLocal(Day, Ten),
            EndDateTime = BrazilTimeZone.AtLocal(Day, Eleven),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 10m,
            DepositPaid = 0m,
        });
        await db.SaveChangesAsync();

        await BookingPolicyEnforcement.EnsureCustomerCanBookAsync(
            db,
            tenantA,
            customerId,
            Day,
            CancellationToken.None);

        db.Reservations.Add(new Reservation
        {
            TenantId = tenantA,
            UnitId = Guid.NewGuid(),
            CustomerId = customerId,
            CustomerName = "Same",
            CustomerWhatsApp = "",
            StartDateTime = BrazilTimeZone.AtLocal(Day, Ten),
            EndDateTime = BrazilTimeZone.AtLocal(Day, Eleven),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 10m,
            DepositPaid = 0m,
        });
        await db.SaveChangesAsync();

        var blocked = await Assert.ThrowsAsync<DailyBookingLimitReachedException>(() =>
            BookingPolicyEnforcement.EnsureCustomerCanBookAsync(
                db,
                tenantA,
                customerId,
                Day,
                CancellationToken.None));
        Assert.Equal(1, blocked.Limit);
    }

    [Fact]
    public async Task Resource_type_row_does_not_cap_any_resource()
    {
        var provider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(provider);
        var tenantId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = tenantId,
            ResourceType = RentalAssetType.Location,
            MaxActiveReservationsPerCustomerPerDay = 1,
        });
        db.Reservations.Add(new Reservation
        {
            TenantId = tenantId,
            UnitId = Guid.NewGuid(),
            CustomerId = customerId,
            CustomerName = "Ada",
            CustomerWhatsApp = "",
            StartDateTime = BrazilTimeZone.AtLocal(Day, Ten),
            EndDateTime = BrazilTimeZone.AtLocal(Day, Eleven),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 10m,
            DepositPaid = 0m,
        });
        await db.SaveChangesAsync();

        await BookingPolicyEnforcement.EnsureCustomerCanBookAsync(
            db,
            tenantId,
            customerId,
            Day,
            CancellationToken.None);
    }

    [Fact]
    public async Task BookSlot_uses_the_slot_civil_date()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        await harness.SetLimitAsync(1);
        var first = await harness.SeedSlotAsync(Day, Ten, Eleven);
        var second = await harness.SeedSlotAsync(Day, Eleven, Noon);

        await harness.Schedule.BookSlotAsync(
            harness.CustomerId,
            new BookSlotRequestDto { SlotId = first, UnitId = harness.UnitId, Quantity = 1 },
            CancellationToken.None);

        await Assert.ThrowsAsync<DailyBookingLimitReachedException>(() =>
            harness.Schedule.BookSlotAsync(
                harness.CustomerId,
                new BookSlotRequestDto { SlotId = second, UnitId = harness.UnitId, Quantity = 1 },
                CancellationToken.None));
    }

    [Fact]
    public async Task Bootstrap_gives_booking_policy_to_admin_and_not_customers_read()
    {
        await using var harness = await RbacAccessHarness.CreateAsync();
        await new TenantAccessBootstrapper(
            harness.Db,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantAccessBootstrapper>.Instance).EnsureAsync(harness.Tenant.Id);

        var admin = await harness.Db.Roles.SingleAsync(role => role.Name == SystemRoles.Admin);
        var user = await harness.Db.Roles.SingleAsync(role => role.Name == SystemRoles.User);
        var keys = await harness.Db.RolePermissions
            .Where(grant => grant.RoleId == admin.Id || grant.RoleId == user.Id)
            .Join(
                harness.Db.Permissions,
                grant => grant.PermissionId,
                permission => permission.Id,
                (grant, permission) => new { grant.RoleId, permission.Key })
            .ToListAsync();

        Assert.Contains(keys, grant => grant.RoleId == admin.Id && grant.Key == Permissions.Rentals.BookingPolicyWrite);
        Assert.Contains(keys, grant => grant.RoleId == admin.Id && grant.Key == Permissions.Rentals.BookingPolicyRead);
        Assert.DoesNotContain(keys, grant => grant.RoleId == user.Id && grant.Key == Permissions.Rentals.BookingPolicyWrite);
        Assert.DoesNotContain(keys, grant => grant.Key == Permissions.Core.CustomersRead);
    }

    [Fact]
    public void Civil_today_follows_the_supplied_zone()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var ahead = TenantBookingClock.Resolve("Pacific/Kiritimati");
        var behind = TenantBookingClock.Resolve("Pacific/Honolulu");
        Assert.NotEqual(ahead.Id, behind.Id);

        var aheadToday = BookingHorizon.CivilToday(now, ahead);
        var behindToday = BookingHorizon.CivilToday(now, behind);
        Assert.True(aheadToday > behindToday);
        Assert.Null(BookingHorizon.Effective(null, null));
        Assert.Equal(2, BookingHorizon.Effective(2, 10));
        Assert.Equal(10, BookingHorizon.Effective(null, 10));
        Assert.False(BookingHorizon.IsBeyond(behindToday, behindToday, 0));
        Assert.True(BookingHorizon.IsBeyond(behindToday.AddDays(1), behindToday, 0));
        Assert.False(BookingHorizon.IsBeyond(aheadToday.AddDays(365), aheadToday, 365));
        Assert.True(BookingHorizon.IsBeyond(aheadToday.AddDays(366), aheadToday, 365));
        Assert.False(BookingHorizon.IsBeyond(new DateOnly(2099, 12, 31), aheadToday, null));
    }

    [Fact]
    public async Task Boundary_day_is_allowed_and_the_next_day_is_rejected_in_the_tenant_zone()
    {
        var now = new DateTimeOffset(2026, 10, 17, 2, 0, 0, TimeSpan.Zero);
        var zone = TenantBookingClock.Resolve(null);
        var today = BookingHorizon.CivilToday(now, zone);
        Assert.NotEqual(DateOnly.FromDateTime(now.UtcDateTime), today);

        const int limit = 1;
        var boundary = today.AddDays(limit);
        var beyond = today.AddDays(limit + 1);
        await using var harness = await BookingPolicyHarness.CreateAsync(today, now);
        await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { MaxAdvanceBookingDays = limit },
            CancellationToken.None);

        var availability = await harness.Reservations.CheckAvailabilityAsync(
            new CheckAvailabilityRequestDto
            {
                AssetId = harness.AssetId,
                Date = boundary,
                StartTime = Ten,
                EndTime = Eleven,
            },
            CancellationToken.None);
        Assert.True(availability.IsAvailable);

        var created = await harness.BookAsync(boundary, Ten, Eleven);
        Assert.Equal(TenantBookingClock.AtLocal(boundary, Ten, zone), created.StartDateTime);

        var rejected = await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            harness.BookAsync(beyond, Ten, Eleven));
        Assert.Equal(BookingHorizonExceededException.DefaultMessage, rejected.Message);
        Assert.Equal("booking_horizon_exceeded", BookingHorizonExceededException.Code);
        Assert.DoesNotContain("court", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quadra", rejected.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            harness.Reservations.CheckAvailabilityAsync(
                new CheckAvailabilityRequestDto
                {
                    AssetId = harness.AssetId,
                    Date = beyond,
                    StartTime = Ten,
                    EndTime = Eleven,
                },
                CancellationToken.None));

        var rentalId = await harness.Db.RentalAssets
            .Where(rental => rental.AssetId == harness.AssetId)
            .Select(rental => rental.Id)
            .SingleAsync();
        var openDay = await harness.Schedule.GetDayAsync(
            boundary, rentalId, customerFacing: true, CancellationToken.None);
        Assert.NotEmpty(openDay.Slots);
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            harness.Schedule.GetDayAsync(
                beyond, rentalId, customerFacing: true, CancellationToken.None));
        var adminDay = await harness.Schedule.GetDayAsync(
            beyond, rentalId, customerFacing: false, CancellationToken.None);
        Assert.NotEmpty(adminDay.Slots);

        var openSlot = await harness.SeedSlotAsync(boundary, Noon, One);
        var booked = await harness.Schedule.BookSlotAsync(
            harness.CustomerId,
            new BookSlotRequestDto { SlotId = openSlot, UnitId = harness.UnitId },
            CancellationToken.None);
        Assert.Equal(TenantBookingClock.AtLocal(boundary, Noon, zone), booked.StartDateTime);

        var slotId = await harness.SeedSlotAsync(beyond, Ten, Eleven);
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            harness.Schedule.BookSlotAsync(
                harness.CustomerId,
                new BookSlotRequestDto { SlotId = slotId, UnitId = harness.UnitId },
                CancellationToken.None));
        Assert.Equal(2, await harness.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Resource_override_wins_null_inherits_and_both_null_is_unlimited()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { MaxAdvanceBookingDays = 10 },
            CancellationToken.None);

        var primary = await harness.Db.RentalAssets.SingleAsync(rental => rental.AssetId == harness.AssetId);
        var secondary = await harness.Db.RentalAssets.SingleAsync(rental => rental.AssetId == harness.SecondAssetId);
        var assets = new RentalAssetService(harness.Db, harness.TenantProvider);
        await assets.SetMaxAdvanceBookingDaysAsync(primary.Id, 2, CancellationToken.None);

        var listed = await assets.ListRentableAsync(CancellationToken.None);
        var primaryDto = Assert.Single(listed, item => item.Id == primary.Id);
        var secondaryDto = Assert.Single(listed, item => item.Id == secondary.Id);
        Assert.Equal(2, primaryDto.MaxAdvanceBookingDays);
        Assert.Equal(2, primaryDto.EffectiveMaxAdvanceBookingDays);
        Assert.Null(secondaryDto.MaxAdvanceBookingDays);
        Assert.Equal(10, secondaryDto.EffectiveMaxAdvanceBookingDays);

        await harness.BookAsync(Day.AddDays(2), Ten, Eleven);
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            harness.BookAsync(Day.AddDays(3), Ten, Eleven));
        await harness.BookAsOtherCustomerAsync(Day.AddDays(10), Ten, Eleven);
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            harness.BookAsOtherCustomerAsync(Day.AddDays(11), Ten, Eleven));

        var open = await harness.Schedule.GetDayAsync(
            Day.AddDays(3), rentalAssetId: null, customerFacing: true, CancellationToken.None);
        Assert.NotEmpty(open.Slots);
        Assert.All(open.Slots, slot => Assert.Equal(secondary.Id, slot.RentalAssetId));
        await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            harness.Schedule.GetDayAsync(
                Day.AddDays(3), primary.Id, customerFacing: true, CancellationToken.None));

        await assets.SetMaxAdvanceBookingDaysAsync(primary.Id, null, CancellationToken.None);
        var inherited = Assert.Single(
            await assets.ListRentableAsync(CancellationToken.None),
            item => item.Id == primary.Id);
        Assert.Null(inherited.MaxAdvanceBookingDays);
        Assert.Equal(10, inherited.EffectiveMaxAdvanceBookingDays);

        await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { MaxAdvanceBookingDays = null },
            CancellationToken.None);
        var far = await harness.BookAsync(new DateOnly(2099, 12, 31), Ten, Eleven);
        Assert.Equal(
            TenantBookingClock.AtLocal(new DateOnly(2099, 12, 31), Ten, TenantBookingClock.Resolve(null)),
            far.StartDateTime);

        var negative = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.Policies.UpsertAsync(
                new UpsertBookingPolicyRequestDto { MaxAdvanceBookingDays = -1 },
                CancellationToken.None));
        Assert.Contains("0 to 3650", negative.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            assets.SetMaxAdvanceBookingDaysAsync(primary.Id, 3651, CancellationToken.None));

        harness.Db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = harness.TenantId,
            ResourceType = RentalAssetType.Location,
            MaxAdvanceBookingDays = 0,
        });
        await harness.Db.SaveChangesAsync();
        var stillUnlimited = await harness.BookAsync(new DateOnly(2099, 12, 30), new TimeOnly(14, 0), new TimeOnly(15, 0));
        Assert.NotEqual(Guid.Empty, stillUnlimited.Id);
    }

    [Fact]
    public async Task One_tenant_horizon_does_not_apply_to_another_tenant()
    {
        var database = Guid.NewGuid().ToString("N");
        await using var left = await BookingPolicyHarness.CreateAsync(
            Day, new TimeOnly(8, 0), database, "11111111000191");
        await left.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { MaxAdvanceBookingDays = 1 },
            CancellationToken.None);
        var leftRental = await left.Db.RentalAssets.SingleAsync(rental => rental.AssetId == left.AssetId);
        await new RentalAssetService(left.Db, left.TenantProvider)
            .SetMaxAdvanceBookingDaysAsync(leftRental.Id, 0, CancellationToken.None);

        await using var right = await BookingPolicyHarness.CreateAsync(
            Day, new TimeOnly(8, 0), database, "22222222000191");
        var rightPolicy = await right.Policies.GetAsync(CancellationToken.None);
        Assert.Null(rightPolicy.MaxAdvanceBookingDays);
        var booked = await right.BookAsync(new DateOnly(2099, 12, 31), Ten, Eleven);
        Assert.Equal(right.TenantId, booked.TenantId);

        var rightAssets = new RentalAssetService(right.Db, right.TenantProvider);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            rightAssets.SetMaxAdvanceBookingDaysAsync(leftRental.Id, 4, CancellationToken.None));
        var leftStored = await left.Db.RentalAssets
            .IgnoreQueryFilters()
            .SingleAsync(rental => rental.Id == leftRental.Id);
        Assert.Equal(0, leftStored.MaxAdvanceBookingDays);

        await Assert.ThrowsAsync<BookingHorizonExceededException>(() =>
            left.BookAsync(Day.AddDays(1), Ten, Eleven));
        Assert.Equal(0, await left.Db.Reservations.CountAsync());
    }

    [Fact]
    public async Task Unknown_or_inactive_resource_past_the_horizon_returns_an_empty_day()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));
        await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { MaxAdvanceBookingDays = 1 },
            CancellationToken.None);

        var far = new DateOnly(2099, 12, 31);
        var primary = await harness.Db.RentalAssets.SingleAsync(rental => rental.AssetId == harness.AssetId);
        var secondary = await harness.Db.RentalAssets.SingleAsync(rental => rental.AssetId == harness.SecondAssetId);
        await harness.SeedSlotAsync(far, Ten, Eleven);
        primary.IsActive = false;
        await harness.Db.SaveChangesAsync();

        var adminDay = await harness.Schedule.GetDayAsync(
            far, rentalAssetId: null, customerFacing: false, CancellationToken.None);
        Assert.Contains(adminDay.Slots, slot => slot.RentalAssetId == secondary.Id);
        Assert.Contains(adminDay.Slots, slot => slot.RentalAssetId == primary.Id);

        var inactiveDay = await harness.Schedule.GetDayAsync(
            far, primary.Id, customerFacing: true, CancellationToken.None);
        Assert.Empty(inactiveDay.Slots);

        var unknownDay = await harness.Schedule.GetDayAsync(
            far, Guid.NewGuid(), customerFacing: true, CancellationToken.None);
        Assert.Empty(unknownDay.Slots);
    }

    [Fact]
    public async Task Public_day_without_ids_hides_persisted_slots_of_inactive_or_non_rentable_resources()
    {
        var now = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);
        var today = BookingHorizon.CivilToday(now, TenantBookingClock.Resolve(null));
        var date = today.AddDays(1);
        Assert.True(BookingHorizon.IsBeyond(date, today, 0));
        Assert.False(BookingHorizon.IsBeyond(date, today, 30));

        await using var harness = await BookingPolicyHarness.CreateAsync(today, now);
        await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { MaxAdvanceBookingDays = 30 },
            CancellationToken.None);

        var primary = await harness.Db.RentalAssets
            .Include(rental => rental.Asset)
            .SingleAsync(rental => rental.AssetId == harness.AssetId);
        var secondary = await harness.Db.RentalAssets
            .SingleAsync(rental => rental.AssetId == harness.SecondAssetId);
        primary.MaxAdvanceBookingDays = 0;
        primary.IsActive = false;
        await harness.Db.SaveChangesAsync();

        var hiddenSlot = await harness.SeedSlotAsync(date, Ten, Eleven);

        var day = await harness.Schedule.GetDayAsync(
            date, rentalAssetId: null, customerFacing: true, CancellationToken.None);
        Assert.DoesNotContain(day.Slots, slot => slot.Id == hiddenSlot);
        Assert.DoesNotContain(day.Slots, slot => slot.RentalAssetId == primary.Id);
        Assert.Contains(day.Slots, slot => slot.RentalAssetId == secondary.Id);

        var adminDay = await harness.Schedule.GetDayAsync(
            date, rentalAssetId: null, customerFacing: false, CancellationToken.None);
        Assert.Contains(adminDay.Slots, slot => slot.Id == hiddenSlot);

        primary.IsActive = true;
        primary.Asset.IsRentable = false;
        await harness.Db.SaveChangesAsync();

        var nonRentableDay = await harness.Schedule.GetDayAsync(
            date, rentalAssetId: null, customerFacing: true, CancellationToken.None);
        Assert.DoesNotContain(nonRentableDay.Slots, slot => slot.Id == hiddenSlot);
        Assert.DoesNotContain(nonRentableDay.Slots, slot => slot.RentalAssetId == primary.Id);
        Assert.Contains(nonRentableDay.Slots, slot => slot.RentalAssetId == secondary.Id);
    }

    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static UpsertBookingPolicyRequestDto DeserializePolicy(string json) =>
        JsonSerializer.Deserialize<UpsertBookingPolicyRequestDto>(json, ApiJson)
        ?? throw new InvalidOperationException("Booking policy JSON was empty.");

    private static UpdateRentableRequest DeserializeRentable(int? horizon, bool includeHorizon = false)
    {
        var horizonJson = horizon is int days
            ? $""","maxAdvanceBookingDays":{days}"""
            : includeHorizon
                ? ""","maxAdvanceBookingDays":null"""
                : "";
        var json = $$"""
            {"name":"Resource","tag":"R-1","unitId":"00000000-0000-0000-0000-000000000001","categoryId":"00000000-0000-0000-0000-000000000002","familyId":"00000000-0000-0000-0000-000000000003"{{horizonJson}}}
            """;
        return JsonSerializer.Deserialize<UpdateRentableRequest>(json, ApiJson)
            ?? throw new InvalidOperationException("Rentable JSON was empty.");
    }

    private sealed class BookingPolicyHarness : IAsyncDisposable
    {
        private BookingPolicyHarness(
            AppDbContext db,
            FakeTenantProvider tenantProvider,
            Guid tenantId,
            Guid unitId,
            Guid customerId,
            Guid otherCustomerId,
            Guid assetId,
            Guid secondAssetId,
            ReservationService reservations,
            ScheduleService schedule,
            BookingPolicyService policies,
            OccupancyKindService kinds)
        {
            Db = db;
            TenantProvider = tenantProvider;
            TenantId = tenantId;
            UnitId = unitId;
            CustomerId = customerId;
            OtherCustomerId = otherCustomerId;
            AssetId = assetId;
            SecondAssetId = secondAssetId;
            Reservations = reservations;
            Schedule = schedule;
            Policies = policies;
            Kinds = kinds;
        }

        public AppDbContext Db { get; }

        public FakeTenantProvider TenantProvider { get; }

        public Guid TenantId { get; }

        public Guid UnitId { get; }

        public Guid CustomerId { get; }

        public Guid OtherCustomerId { get; }

        public Guid AssetId { get; }

        public Guid SecondAssetId { get; }

        public ReservationService Reservations { get; }

        public ScheduleService Schedule { get; }

        public BookingPolicyService Policies { get; }

        public OccupancyKindService Kinds { get; }

        public static Task<BookingPolicyHarness> CreateAsync(DateOnly day, TimeOnly clock) =>
            CreateAsync(day, BrazilTimeZone.AtLocal(day, clock), databaseName: null, taxId: null);

        public static Task<BookingPolicyHarness> CreateAsync(DateOnly day, DateTimeOffset utcNow) =>
            CreateAsync(day, utcNow, databaseName: null, taxId: null);

        public static Task<BookingPolicyHarness> CreateAsync(
            DateOnly day,
            TimeOnly clock,
            string databaseName,
            string taxId) =>
            CreateAsync(day, BrazilTimeZone.AtLocal(day, clock), databaseName, taxId);

        public static async Task<BookingPolicyHarness> CreateAsync(
            DateOnly day,
            DateTimeOffset utcNow,
            string? databaseName,
            string? taxId)
        {
            _ = day;
            var tenantProvider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(tenantProvider, databaseName);
            var clock = new TestTimeProvider(utcNow);
            var tenant = new Tenant(
                "North Club",
                taxId ?? UniqueTaxId(),
                subdomain: $"club-{Guid.NewGuid():N}"[..20]);
            var unit = new Unit(tenant.Id, "Main");
            var category = new AssetCategory { TenantId = tenant.Id, Name = "Spaces" };
            var family = new AssetFamily
            {
                Key = $"spaces-{Guid.NewGuid():N}"[..32],
                Label = "Spaces",
                FieldSchemaJson = "{}",
            };
            var primary = CreateAsset(tenant.Id, unit.Id, category.Id, family.Id, "Studio A", "SA");
            var secondary = CreateAsset(tenant.Id, unit.Id, category.Id, family.Id, "Studio B", "SB");
            var primaryRental = CreateRental(tenant.Id, primary.Id, requiresDeposit: false);
            var secondaryRental = CreateRental(tenant.Id, secondary.Id, requiresDeposit: false);
            var customer = new Customer
            {
                TenantId = tenant.Id,
                Name = "Ada",
                Email = $"ada-{Guid.NewGuid():N}@club.test",
                Phone = UniquePhone(),
            };
            var other = new Customer
            {
                TenantId = tenant.Id,
                Name = "Bea",
                Email = $"bea-{Guid.NewGuid():N}@club.test",
                Phone = UniquePhone(),
            };

            tenantProvider.TenantId = tenant.Id;
            db.Tenants.Add(tenant);
            db.Units.Add(unit);
            db.AssetCategories.Add(category);
            db.AssetFamilies.Add(family);
            db.Assets.AddRange(primary, secondary);
            db.RentalAssets.AddRange(primaryRental, secondaryRental);
            db.Customers.AddRange(customer, other);
            foreach (var rental in new[] { primaryRental, secondaryRental })
            {
                foreach (var weekDay in Enum.GetValues<DayOfWeek>())
                {
                    db.RentalPricings.Add(new RentalPricing
                    {
                        TenantId = tenant.Id,
                        RentalAssetId = rental.Id,
                        DayOfWeek = weekDay,
                        StartTime = TimeOnly.MinValue,
                        EndTime = new TimeOnly(23, 59),
                        PricePerHour = 40m,
                        RequiresDeposit = false,
                        DepositPercentage = 0m,
                    });
                }
            }

            await db.SaveChangesAsync();
            return Build(db, tenantProvider, tenant.Id, unit.Id, customer.Id, other.Id, primary.Id, secondary.Id, clock);
        }

        public Task<ReservationResponseDto> BookAsync(DateOnly date, TimeOnly start, TimeOnly end) =>
            BookAsync(CustomerId, AssetId, date, start, end);

        public Task<ReservationResponseDto> BookAsOtherCustomerAsync(DateOnly date, TimeOnly start, TimeOnly end) =>
            BookAsync(OtherCustomerId, SecondAssetId, date, start, end);

        public Task SetLimitAsync(int? limit) =>
            Policies.UpsertAsync(
                new UpsertBookingPolicyRequestDto { MaxActiveReservationsPerCustomerPerDay = limit },
                CancellationToken.None);

        public async Task<BookingPolicyHarness> WithDepositRequiredAsync()
        {
            var rental = await Db.RentalAssets.SingleAsync(item => item.AssetId == AssetId);
            rental.RequiresDeposit = true;
            await Db.SaveChangesAsync();
            return this;
        }

        public async Task<Guid> SeedSlotAsync(DateOnly date, TimeOnly start, TimeOnly end)
        {
            await Kinds.EnsureDefaultsAsync(CancellationToken.None);
            var openKind = await Db.OccupancyKinds.SingleAsync(kind => kind.Key == "open");
            var rentalId = await Db.RentalAssets
                .Where(rental => rental.AssetId == AssetId)
                .Select(rental => rental.Id)
                .SingleAsync();
            var slot = new Slot
            {
                TenantId = TenantId,
                RentalAssetId = rentalId,
                Date = date,
                StartTime = start,
                EndTime = end,
                OccupancyKindId = openKind.Id,
                Status = SlotStatus.Available,
            };
            Db.Slots.Add(slot);
            await Db.SaveChangesAsync();
            return slot.Id;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private Task<ReservationResponseDto> BookAsync(
            Guid customerId,
            Guid assetId,
            DateOnly date,
            TimeOnly start,
            TimeOnly end) =>
            Reservations.CreateReservationAsync(
                customerId,
                new CreateReservationRequestDto
                {
                    UnitId = UnitId,
                    Date = date,
                    StartTime = start,
                    EndTime = end,
                    Items = [new CreateReservationItemRequestDto { AssetId = assetId, Quantity = 1 }],
                },
                CancellationToken.None);

        private static BookingPolicyHarness Build(
            AppDbContext db,
            FakeTenantProvider tenantProvider,
            Guid tenantId,
            Guid unitId,
            Guid customerId,
            Guid otherCustomerId,
            Guid assetId,
            Guid secondAssetId,
            TimeProvider clock)
        {
            var kinds = new OccupancyKindService(db, tenantProvider);
            var queue = TestReservationQueue.Create(db, tenantProvider, clock);
            var reservations = new ReservationService(
                db,
                tenantProvider,
                new FakeTrialGuard(),
                queue,
                SilentRentalsNotifications.Publisher.AsDomainEvents(),
                SilentRentalsNotifications.Scheduler,
                clock);
            var schedule = new ScheduleService(
                db,
                tenantProvider,
                kinds,
                new FakeTrialGuard(),
                queue,
                SilentRentalsNotifications.Publisher.AsDomainEvents(),
                SilentRentalsNotifications.Scheduler,
                clock);
            return new BookingPolicyHarness(
                db,
                tenantProvider,
                tenantId,
                unitId,
                customerId,
                otherCustomerId,
                assetId,
                secondAssetId,
                reservations,
                schedule,
                new BookingPolicyService(db, tenantProvider),
                kinds);
        }

        private static Asset CreateAsset(
            Guid tenantId,
            Guid unitId,
            Guid categoryId,
            Guid familyId,
            string name,
            string tag) =>
            new()
            {
                TenantId = tenantId,
                UnitId = unitId,
                CategoryId = categoryId,
                FamilyId = familyId,
                Name = name,
                Tag = tag,
                Status = AssetStatus.Active,
                IsRentable = true,
            };

        private static RentalAsset CreateRental(Guid tenantId, Guid assetId, bool requiresDeposit) =>
            new()
            {
                TenantId = tenantId,
                AssetId = assetId,
                Type = RentalAssetType.Location,
                TotalQuantity = 1,
                IsActive = true,
                RequiresDeposit = requiresDeposit,
                SchedulePolicy = SchedulePolicy.OpenHours,
                OpenTime = TimeOnly.MinValue,
                CloseTime = new TimeOnly(23, 59),
                AllowedDurationMinutes = "60",
                QueueEnabled = false,
            };

        private static string UniqueTaxId() => Guid.NewGuid().ToString("N")[..14];

        private static string UniquePhone() => $"119{Random.Shared.Next(10000000, 99999999)}";
    }
}
