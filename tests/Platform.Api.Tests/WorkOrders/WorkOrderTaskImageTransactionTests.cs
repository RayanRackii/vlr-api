using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authorization;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Notifications;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.WorkOrders;

public sealed class WorkOrderTaskImageTransactionTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public Task Commit_acknowledgement_failure_preserves_the_referenced_blob() =>
        AssertUploadFailureAsync(commitApplied: true);

    [DockerFact]
    public Task Replacement_commit_acknowledgement_failure_preserves_both_blobs_and_logs_both_keys() =>
        AssertUploadFailureAsync(commitApplied: true, replacing: true);

    [DockerFact]
    public Task Failure_before_commit_rolls_back_and_deletes_the_new_blob() =>
        AssertUploadFailureAsync(commitApplied: false);

    private async Task AssertUploadFailureAsync(bool commitApplied, bool replacing = false)
    {
        var factory = postgres.Factory
            ?? throw new InvalidOperationException("PostgreSQL fixture was not initialized.");
        var tenantProvider = new FakeTenantProvider();
        await using var seed = factory.Create(tenantProvider);
        var tenant = new Tenant(
            "Image Club",
            Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
            subdomain: $"image-{Guid.NewGuid():N}"[..20]);
        tenantProvider.TenantId = tenant.Id;
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..20],
            Label = "Spaces",
            FieldSchemaJson = """{"fields":[]}""",
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Quadra 1",
            Tag = "Q1",
            Status = AssetStatus.Active,
        };
        var order = new WorkOrder
        {
            TenantId = tenant.Id,
            AssetId = asset.Id,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = new DateOnly(2026, 10, 18),
        };
        var task = new WorkOrderTask
        {
            TenantId = tenant.Id,
            WorkOrderId = order.Id,
            Title = "Foto da quadra",
            InputType = TaskInputType.Image,
            IsMandatory = true,
            Order = 1,
        };
        order.AddTask(task);
        seed.AddRange(tenant, unit, category, family, asset, order);
        WorkOrderTaskFile? previousFile = null;
        if (replacing)
        {
            previousFile = new WorkOrderTaskFile
            {
                TenantId = tenant.Id,
                WorkOrderId = order.Id,
                WorkOrderTaskId = task.Id,
                StorageKey = WorkOrderImageRules.StorageKey(tenant.Id, order.Id, task.Id, Guid.NewGuid()),
                FileName = "previous.jpg",
                ContentType = "image/jpeg",
                SizeBytes = 4,
            };
            task.Value = previousFile.Id.ToString("D");
            seed.Add(previousFile);
        }

        await seed.SaveChangesAsync();

        var failure = new IOException("Database acknowledgement lost.");
        IInterceptor interceptor = commitApplied
            ? new CommitAcknowledgementFailure(failure)
            : new SaveAcknowledgementFailure(failure);
        await using var writer = factory.Create(tenantProvider, interceptor);
        var storage = new RecordingStorageProvider();
        var logger = new RecordingLogger();
        var service = CreateService(writer, tenantProvider, storage, logger);
        await using var content = new MemoryStream([0xFF, 0xD8, 0xFF, 0x00]);

        var thrown = await Assert.ThrowsAsync<IOException>(() => service.UploadAsync(
            order.Id, task.Id, "court.jpg", "image/jpeg", content, CancellationToken.None));

        Assert.Same(failure, thrown);
        Assert.NotNull(storage.UploadedKey);
        await using var verify = factory.Create(tenantProvider);
        var storedFile = await verify.WorkOrderTaskFiles.SingleOrDefaultAsync();
        var storedTask = await verify.WorkOrderTasks.SingleAsync(item => item.Id == task.Id);
        if (commitApplied)
        {
            Assert.NotNull(storedFile);
            Assert.Equal(storage.UploadedKey, storedFile.StorageKey);
            Assert.Equal(storedFile.Id.ToString("D"), storedTask.Value);
            Assert.Empty(storage.DeletedKeys);
            var warning = Assert.Single(logger.Warnings);
            Assert.Contains(storage.UploadedKey, warning, StringComparison.Ordinal);
            Assert.Contains("catalog-private", warning, StringComparison.Ordinal);
            Assert.Contains("reconciliation", warning, StringComparison.Ordinal);
            if (previousFile is not null)
            {
                Assert.Equal(previousFile.Id, storedFile.Id);
                Assert.NotEqual(previousFile.StorageKey, storedFile.StorageKey);
                Assert.Contains(previousFile.StorageKey, warning, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Null(storedFile);
            Assert.Null(storedTask.Value);
            Assert.Equal(storage.UploadedKey, Assert.Single(storage.DeletedKeys));
            Assert.Empty(logger.Warnings);
        }
    }

    private static WorkOrderTaskImageService CreateService(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        IStorageProvider storage,
        ILogger<WorkOrderTaskImageService> logger)
    {
        var http = new FakeHttpContextAccessor();
        http.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("email", "ops@tenant.test"), new Claim("sub", "admin-sub")],
                authenticationType: "Test")),
        };
        var resolver = TestPermissionResolvers.Create(db, tenantProvider);
        var users = new UserDirectoryService(
            db,
            tenantProvider,
            new FakePlatformAdminChecker("ops@tenant.test"),
            resolver,
            new RbacGrantGuard(db, resolver, NullLogger<RbacGrantGuard>.Instance),
            new FakeTrialGuard(),
            new NotificationQueue(),
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment(),
            NullLogger<UserDirectoryService>.Instance);
        return new WorkOrderTaskImageService(
            db, tenantProvider, http, users, resolver, storage, Options.Create(new StorageOptions()), logger);
    }

    private sealed class CommitAcknowledgementFailure(Exception failure) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class SaveAcknowledgementFailure(Exception failure) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class RecordingStorageProvider : IStorageProvider
    {
        public string? UploadedKey { get; private set; }

        public List<string> DeletedKeys { get; } = [];

        public Task UploadAsync(
            string bucket,
            string key,
            Stream stream,
            string contentType,
            CancellationToken cancellationToken = default)
        {
            UploadedKey = key;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(key);
            return Task.CompletedTask;
        }

        public string GetPublicUrl(string bucket, string key) => throw new NotSupportedException();

        public Task<string> CreateSignedUrlAsync(
            string bucket, string key, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<WorkOrderTaskImageService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
