using Platform.Api.Modules.WorkOrders.Dtos;

namespace Platform.Api.Modules.WorkOrders.Services;

public interface IWorkOrderTaskImageService
{
    Task<WorkOrderTaskFileResponse?> UploadAsync(
        Guid workOrderId,
        Guid taskId,
        string? fileName,
        string? contentType,
        Stream content,
        CancellationToken cancellationToken);

    Task<WorkOrderTaskFileUrlResponse?> GetUrlAsync(
        Guid workOrderId,
        Guid taskId,
        Guid fileId,
        CancellationToken cancellationToken);
}
