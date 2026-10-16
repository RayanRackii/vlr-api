using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
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
    public async Task Missing_policy_is_unlimited_and_stores_sanitized_plain_text()
    {
        await using var harness = await BookingPolicyHarness.CreateAsync(Day, new TimeOnly(8, 0));

        var before = await harness.Policies.GetAsync(CancellationToken.None);
        Assert.Null(before.CancellationPolicyText);
        Assert.Null(before.MaxActiveReservationsPerCustomerPerDay);

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

        var clearedText = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { MaxActiveReservationsPerCustomerPerDay = 4 },
            CancellationToken.None);
        Assert.Null(clearedText.CancellationPolicyText);
        Assert.Equal(4, clearedText.MaxActiveReservationsPerCustomerPerDay);

        var clearedLimit = await harness.Policies.UpsertAsync(
            new UpsertBookingPolicyRequestDto { CancellationPolicyText = "Same note" },
            CancellationToken.None);
        Assert.Equal("Same note", clearedLimit.CancellationPolicyText);
        Assert.Null(clearedLimit.MaxActiveReservationsPerCustomerPerDay);
    }

    [Fact]
    public void Missing_tenant_timezone_falls_back_to_America_Sao_Paulo()
    {
        var tenant = new Tenant("North Club", "12345678000199");
        Assert.Null(TenantBookingClock.ReadConfiguredTimeZoneId(tenant));

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
        Assert.Null(TenantBookingClock.ReadConfiguredTimeZoneId(tenant));
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
        await new TenantAccessBootstrapper(harness.Db).EnsureAsync(harness.Tenant.Id);

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
                SilentRentalsNotifications.Publisher,
                SilentRentalsNotifications.Scheduler,
                clock);
            var schedule = new ScheduleService(
                db,
                tenantProvider,
                kinds,
                new FakeTrialGuard(),
                queue,
                SilentRentalsNotifications.Publisher,
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
