using Platform.Api.Storage;

namespace Platform.Api.Tests.Fakes;

public sealed class RecordingStorageProvider : IStorageProvider
{
    public List<(string Bucket, string Prefix)> DeletedPrefixes { get; } = [];

    public bool FailUpload { get; set; }

    public Func<CancellationToken, Task>? BeforeUploadAsync { get; set; }

    public bool FailPrefixDeletion { get; set; }

    public async Task UploadAsync(string bucket, string key, Stream stream, string contentType, CancellationToken cancellationToken = default)
    {
        if (BeforeUploadAsync is not null) await BeforeUploadAsync(cancellationToken);
        Uploads.Add((bucket, key));
        Objects.Add((bucket, key));
        if (FailUpload) throw new InvalidOperationException("Upload acknowledgement lost.");
    }

    public string GetPublicUrl(string bucket, string key) => $"https://storage.test/{bucket}/{key}";

    public Task<string> CreateSignedUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        Signed.Add((bucket, key));
        SignedTtls.Add(ttl);
        return Task.FromResult($"https://storage.test/sign/{bucket}/{key}");
    }

    public List<(string Bucket, string Key)> Uploads { get; } = [];

    public List<TimeSpan> SignedTtls { get; } = [];

    public List<(string Bucket, string Key)> Deletes { get; } = [];

    public List<(string Bucket, string Key)> Signed { get; } = [];

    public HashSet<(string Bucket, string Key)> Objects { get; } = [];

    public bool FailRequiredDelete { get; set; }

    public Task DeleteRequiredAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (FailRequiredDelete) return Task.FromException(new InvalidOperationException("Storage unavailable."));
        return DeleteAsync(bucket, key, cancellationToken);
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        Deletes.Add((bucket, key));
        Objects.Remove((bucket, key));
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string bucket, string prefix, CancellationToken cancellationToken = default)
    {
        DeletedPrefixes.Add((bucket, prefix));
        return FailPrefixDeletion
            ? Task.FromException(new InvalidOperationException("Storage unavailable."))
            : Task.CompletedTask;
    }
}
