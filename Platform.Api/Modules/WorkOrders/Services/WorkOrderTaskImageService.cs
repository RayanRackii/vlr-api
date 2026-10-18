using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Authorization;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.WorkOrders.Services;

public sealed class WorkOrderTaskImageService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IHttpContextAccessor httpContextAccessor,
    IUserDirectoryService userDirectoryService,
    IPermissionResolver permissionResolver,
    IStorageProvider storageProvider,
    IOptions<StorageOptions> storageOptions,
    ILogger<WorkOrderTaskImageService> logger) : IWorkOrderTaskImageService
{
    public async Task<WorkOrderTaskFileResponse?> UploadAsync(
        Guid workOrderId,
        Guid taskId,
        string? fileName,
        string? contentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var tenantId = WorkOrderExecution.RequireTenant(tenantProvider);
        var bytes = await ReadLimitedAsync(content, cancellationToken);
        var mediaType = WorkOrderImageRules.Validate(contentType, bytes.Length, bytes);

        var accessible = await WorkOrderExecution.LoadAccessibleAsync(
            dbContext,
            tenantProvider,
            httpContextAccessor,
            userDirectoryService,
            permissionResolver,
            workOrderId,
            cancellationToken);
        var accessibleTask = accessible?.Tasks.FirstOrDefault(item => item.Id == taskId);
        if (accessible is null || accessibleTask is null)
        {
            return null;
        }

        if (accessibleTask.InputType != TaskInputType.Image)
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.NotAnImageTask,
                "Only image tasks accept a photo.");
        }

        var storageKey = WorkOrderImageRules.StorageKey(
            tenantId,
            accessible.Id,
            accessibleTask.Id,
            Guid.NewGuid());
        var bucket = storageOptions.Value.PrivateBucket;
        UploadOutcome outcome;
        try
        {
            await using (var payload = new MemoryStream(bytes, writable: false))
            {
                await storageProvider.UploadAsync(
                    bucket,
                    storageKey,
                    payload,
                    mediaType,
                    cancellationToken);
            }

            outcome = await InTransactionAsync(
                () => SwapStoredFileAsync(
                    tenantId,
                    workOrderId,
                    taskId,
                    fileName,
                    mediaType,
                    bytes.Length,
                    storageKey,
                    cancellationToken),
                cancellationToken);
        }
        catch
        {
            await TryDeleteAsync(bucket, storageKey);
            throw;
        }

        if (outcome.Response is null)
        {
            await TryDeleteAsync(bucket, storageKey);
            return null;
        }

        if (outcome.PreviousKey is not null
            && !string.Equals(outcome.PreviousKey, storageKey, StringComparison.Ordinal)
            && WorkOrderImageRules.IsKeyForTask(outcome.PreviousKey, tenantId, workOrderId, taskId))
        {
            await TryDeleteAsync(bucket, outcome.PreviousKey);
        }

        return outcome.Response;
    }

    private async Task<UploadOutcome> SwapStoredFileAsync(
        Guid tenantId,
        Guid workOrderId,
        Guid taskId,
        string? fileName,
        string mediaType,
        long sizeBytes,
        string storageKey,
        CancellationToken cancellationToken)
    {
        var workOrder = await WorkOrderExecution.LockAndLoadAsync(
            dbContext,
            tenantProvider,
            httpContextAccessor,
            userDirectoryService,
            permissionResolver,
            tenantId,
            workOrderId,
            cancellationToken);
        if (workOrder is null)
        {
            return UploadOutcome.Missing;
        }

        WorkOrderExecution.EnsureMutable(workOrder);
        var task = workOrder.Tasks.FirstOrDefault(item => item.Id == taskId);
        if (task is null)
        {
            return UploadOutcome.Missing;
        }

        if (task.InputType != TaskInputType.Image)
        {
            throw new InvalidTaskValueException(
                InvalidTaskValueException.NotAnImageTask,
                "Only image tasks accept a photo.");
        }

        var existing = await dbContext.WorkOrderTaskFiles
            .FirstOrDefaultAsync(file => file.WorkOrderTaskId == task.Id, cancellationToken);
        var previousKey = existing?.StorageKey;
        var safeName = WorkOrderImageRules.SanitizeFileName(fileName);
        var file = existing ?? new WorkOrderTaskFile
        {
            TenantId = tenantId,
            WorkOrderId = workOrder.Id,
            WorkOrderTaskId = task.Id,
            StorageKey = storageKey,
            FileName = safeName,
            ContentType = mediaType,
            SizeBytes = sizeBytes,
        };

        if (existing is null)
        {
            dbContext.WorkOrderTaskFiles.Add(file);
        }
        else
        {
            existing.StorageKey = storageKey;
            existing.FileName = safeName;
            existing.ContentType = mediaType;
            existing.SizeBytes = sizeBytes;
            existing.Touch();
        }

        task.Value = file.Id.ToString("D");
        task.Touch();
        workOrder.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);
        return new UploadOutcome(
            new WorkOrderTaskFileResponse(
                file.Id,
                file.FileName,
                file.ContentType,
                file.SizeBytes,
                task.Value!),
            previousKey);
    }

    public async Task<WorkOrderTaskFileUrlResponse?> GetUrlAsync(
        Guid workOrderId,
        Guid taskId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var tenantId = WorkOrderExecution.RequireTenant(tenantProvider);
        var workOrder = await WorkOrderExecution.LoadAccessibleAsync(
            dbContext,
            tenantProvider,
            httpContextAccessor,
            userDirectoryService,
            permissionResolver,
            workOrderId,
            cancellationToken);
        if (workOrder is null || workOrder.Tasks.All(task => task.Id != taskId))
        {
            return null;
        }

        var file = await dbContext.WorkOrderTaskFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == fileId
                    && item.WorkOrderId == workOrder.Id
                    && item.WorkOrderTaskId == taskId,
                cancellationToken);
        if (file is null || !WorkOrderImageRules.IsKeyForTask(file.StorageKey, tenantId, workOrder.Id, taskId))
        {
            return null;
        }

        var ttl = TimeSpan.FromSeconds(Math.Max(60, storageOptions.Value.SignedUrlTtlSeconds));
        var url = await storageProvider.CreateSignedUrlAsync(
            storageOptions.Value.PrivateBucket,
            file.StorageKey,
            ttl,
            cancellationToken);
        return new WorkOrderTaskFileUrlResponse(url, IsPublic: false);
    }

    private async Task TryDeleteAsync(string bucket, string key)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await storageProvider.DeleteAsync(bucket, key, cleanup.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to clean up work order image {Bucket}/{Key}; storage cleanup must be retried.",
                bucket,
                key);
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream content, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > WorkOrderImageRules.MaxBytes)
            {
                throw new WorkOrderImageValidationException(
                    WorkOrderImageRules.TooLarge,
                    "Work order images must be between 1 byte and 5 MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return await action();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private sealed record UploadOutcome(WorkOrderTaskFileResponse? Response, string? PreviousKey)
    {
        public static UploadOutcome Missing { get; } = new(null, null);
    }
}
