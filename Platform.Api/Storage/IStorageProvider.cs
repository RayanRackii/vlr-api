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

    /// <summary>Recursively lists object keys beneath a prefix (empty means the entire bucket).</summary>
    Task<IReadOnlyList<string>> ListAsync(
        string bucket,
        string prefix,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Storage listing is not supported by this provider.");

    /// <summary>Delete that throws when the provider did not remove the object (a missing object is fine).</summary>
    Task DeleteRequiredAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(bucket, key, cancellationToken);

    Task DeletePrefixAsync(
        string bucket,
        string prefix,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Storage prefix deletion is not supported by this provider.");
}
