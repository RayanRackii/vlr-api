using Microsoft.AspNetCore.Mvc;

namespace Platform.Api.Storage;

public static class StorageProviderActionResults
{
    public static ObjectResult From(StorageProviderException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var status = exception.Kind switch
        {
            StorageProviderErrorKind.Client => StatusCodes.Status400BadRequest,
            StorageProviderErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status502BadGateway,
        };

        return new ObjectResult(new { error = exception.Message }) { StatusCode = status };
    }

    public static ObjectResult FromInvalidOperation(InvalidOperationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ObjectResult(new { error = exception.Message })
        {
            StatusCode = StatusCodes.Status500InternalServerError,
        };
    }

    public static ObjectResult FromHttpRequestException() =>
        new ObjectResult(new { error = StorageProviderException.UpstreamMessage })
        {
            StatusCode = StatusCodes.Status502BadGateway,
        };
}
