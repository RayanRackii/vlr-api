using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Api.Storage;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.WorkOrders.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.WorkOrders)]
[Route("api/work-orders")]
public sealed class WorkOrderTaskFilesController(IWorkOrderTaskImageService taskImages) : ControllerBase
{
    /// <summary>
    /// Stores a jpeg, png, or webp (max 5 MB) for an image task on a work order
    /// the caller can execute. The task answer becomes the returned file id.
    /// </summary>
    [HttpPost("{id:guid}/tasks/{taskId:guid}/files")]
    [RequirePermission(Permissions.Os.WorkOrdersExecute)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(WorkOrderImageRules.RequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = WorkOrderImageRules.RequestBytes)]
    public async Task<ActionResult<WorkOrderTaskFileResponse>> Upload(
        Guid id,
        Guid taskId,
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return BadRequest(new { error = "File is required." });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var stored = await taskImages.UploadAsync(
                id,
                taskId,
                file.FileName,
                file.ContentType,
                stream,
                cancellationToken);
            if (stored is null)
            {
                return NotFound();
            }

            return Created($"/api/work-orders/{id}/tasks/{taskId}/files/{stored.Id}/url", stored);
        }
        catch (WorkOrderImageValidationException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code });
        }
        catch (WorkOrderClosedException ex)
        {
            return Conflict(new { error = ex.Message, code = ex.Code });
        }
        catch (InvalidTaskValueException ex)
        {
            return BadRequest(new { error = ex.Message, code = ex.Code, reason = ex.Reason });
        }
        catch (StorageProviderException ex)
        {
            return StorageProviderActionResults.From(ex);
        }
        catch (InvalidOperationException ex)
        {
            return StorageProviderActionResults.FromInvalidOperation(ex);
        }
        catch (HttpRequestException)
        {
            return StorageProviderActionResults.FromHttpRequestException();
        }
    }

    /// <summary>
    /// Returns a short-lived signed URL for a photo stored on an image task.
    /// The object stays in the private catalog bucket. Callers need
    /// <c>os.work_orders.execute</c> and access to the work order.
    /// </summary>
    [HttpGet("{id:guid}/tasks/{taskId:guid}/files/{fileId:guid}/url")]
    [RequirePermission(Permissions.Os.WorkOrdersExecute)]
    public async Task<ActionResult<WorkOrderTaskFileUrlResponse>> GetUrl(
        Guid id,
        Guid taskId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = await taskImages.GetUrlAsync(id, taskId, fileId, cancellationToken);
            return url is null ? NotFound() : Ok(url);
        }
        catch (StorageProviderException ex)
        {
            return StorageProviderActionResults.From(ex);
        }
        catch (InvalidOperationException ex)
        {
            return StorageProviderActionResults.FromInvalidOperation(ex);
        }
        catch (HttpRequestException)
        {
            return StorageProviderActionResults.FromHttpRequestException();
        }
    }
}
