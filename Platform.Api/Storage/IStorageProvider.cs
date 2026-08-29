namespace Platform.Api.Storage;

public interface IStorageProvider
{
    Task UploadAsync(
        string bucket,
        string key,
        Stream stream,
        string contentType,
        CancellationToken cancellationToken = default);

    string GetPublicUrl(string bucket, string key);

    Task<string> CreateSignedUrlAsync(
        string bucket,
        string key,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default);
}
