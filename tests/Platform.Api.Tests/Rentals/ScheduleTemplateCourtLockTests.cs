using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class ScheduleTemplateCourtLockTests
{
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);

    [Fact]
    public async Task Update_refuses_when_court_changes_after_the_lock_ids_were_chosen()
    {
        var interceptor = new MoveTemplateCourtOnFirstRead();
        await using var harness = await TeacherLessonHarness.CreateAsync(interceptors: [interceptor]);
        var otherCourtId = await SeedCourtAsync(harness, "Quadra 2", "Q2");
        var kinds = await harness.EnsureKindsAsync();
        interceptor.Arm(harness.TenantProvider, harness.OpenTemplateId, otherCourtId);
        await using var db = NewContext(harness);
        var schedule = CreateSchedule(db, harness.TenantProvider);

        var exception = await Record.ExceptionAsync(() => schedule.UpdateTemplateAsync(
            harness.OpenTemplateId,
            new UpsertScheduleTemplateRequestDto
            {
                RentalAssetId = harness.RentalAssetId,
                DayOfWeek = DayOfWeek.Tuesday,
                StartTime = Ten,
                EndTime = Eleven,
                OccupancyKindId = kinds.Open.Id,
                Label = "Grade concorrente",
                IsActive = true,
            },
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        var persisted = await harness.Db.ScheduleTemplates.AsNoTracking()
            .SingleAsync(template => template.Id == harness.OpenTemplateId);
        Assert.True(interceptor.Moved);
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("The template court changed before its row was locked.", invalid.Message);
        Assert.Equal(otherCourtId, persisted.RentalAssetId);
        Assert.Null(persisted.Label);
    }

    [Fact]
    public async Task Delete_refuses_when_court_changes_after_the_lock_ids_were_chosen()
    {
        var interceptor = new MoveTemplateCourtOnFirstRead();
        await using var harness = await TeacherLessonHarness.CreateAsync(interceptors: [interceptor]);
        var otherCourtId = await SeedCourtAsync(harness, "Quadra 3", "Q3");
        interceptor.Arm(harness.TenantProvider, harness.OpenTemplateId, otherCourtId);
        await using var db = NewContext(harness);
        var schedule = CreateSchedule(db, harness.TenantProvider);

        var exception = await Record.ExceptionAsync(() =>
            schedule.DeleteTemplateAsync(harness.OpenTemplateId, CancellationToken.None));

        Assert.True(interceptor.Moved);
        var invalid = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("The template court changed before its row was locked.", invalid.Message);
        harness.Db.ChangeTracker.Clear();
        var persisted = await harness.Db.ScheduleTemplates.AsNoTracking()
            .SingleAsync(template => template.Id == harness.OpenTemplateId);
        Assert.Equal(otherCourtId, persisted.RentalAssetId);
    }

    private static AppDbContext NewContext(TeacherLessonHarness harness)
    {
        var options = (DbContextOptions<AppDbContext>)harness.Db.GetService<IDbContextOptions>();
        return new TestAppDbContext(options, harness.TenantProvider);
    }

    private static ScheduleService CreateSchedule(AppDbContext db, FakeTenantProvider tenantProvider) =>
        new(
            db,
            tenantProvider,
            new OccupancyKindService(db, tenantProvider),
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider),
            SilentRentalsNotifications.Publisher.AsDomainEvents(),
            SilentRentalsNotifications.Scheduler,
            TimeProvider.System);

    private static async Task<Guid> SeedCourtAsync(TeacherLessonHarness harness, string name, string tag)
    {
        var asset = new Asset
        {
            TenantId = harness.TenantId,
            UnitId = harness.UnitId,
            CategoryId = (await harness.Db.AssetCategories.SingleAsync()).Id,
            FamilyId = (await harness.Db.AssetFamilies.SingleAsync()).Id,
            Name = name,
            Tag = tag,
            Status = AssetStatus.Active,
            IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = harness.TenantId,
            AssetId = asset.Id,
            Type = RentalAssetType.Location,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = true,
            SchedulePolicy = SchedulePolicy.SlotGrid,
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0),
            QueueEnabled = false,
        };
        harness.Db.Assets.Add(asset);
        harness.Db.RentalAssets.Add(rental);
        await harness.Db.SaveChangesAsync();
        return rental.Id;
    }

    private sealed class MoveTemplateCourtOnFirstRead : IMaterializationInterceptor
    {
        private FakeTenantProvider? _tenantProvider;
        private Guid _templateId;
        private Guid _otherCourtId;
        private int _seen;

        public bool Moved { get; private set; }

        public void Arm(FakeTenantProvider tenantProvider, Guid templateId, Guid otherCourtId)
        {
            _tenantProvider = tenantProvider;
            _templateId = templateId;
            _otherCourtId = otherCourtId;
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (_tenantProvider is null
                || entity is not ScheduleTemplate template
                || template.Id != _templateId
                || Interlocked.Increment(ref _seen) != 1)
            {
                return entity;
            }

            var options = (DbContextOptions<AppDbContext>)materializationData.Context
                .GetService<IDbContextOptions>();
            using var other = new TestAppDbContext(options, _tenantProvider);
            var row = other.ScheduleTemplates.Single(item => item.Id == _templateId);
            row.RentalAssetId = _otherCourtId;
            other.SaveChanges();
            Moved = true;
            return entity;
        }
    }
}
