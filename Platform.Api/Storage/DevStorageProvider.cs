using Microsoft.Extensions.Options;

namespace Platform.Api.Storage;

public sealed class DevStorageProvider(
    IHostEnvironment hostEnvironment,
    IOptions<StorageOptions> options) : IStorageProvider
{
    public async Task UploadAsync(
        string bucket,
        string key,
        Stream stream,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(bucket, key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await stream.CopyToAsync(file, cancellationToken);
    }

    public string GetPublicUrl(string bucket, string key)
    {
        EnsurePublicBucket(bucket);
        return $"https://dev-storage.local/{Uri.EscapeDataString(bucket)}/{key.Replace('\\', '/')}";
    }

    public Task<string> CreateSignedUrlAsync(
        string bucket,
        string key,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var expires = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
        var url =
            $"https://dev-storage.local/{Uri.EscapeDataString(bucket)}/{key.Replace('\\', '/')}?sig=dev&exp={expires}";
        return Task.FromResult(url);
    }

    public Task<IReadOnlyList<string>> ListAsync(
        string bucket,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = ResolvePath(bucket, "");
        var normalized = prefix.Trim('/');
        IReadOnlyList<string> keys = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Where(key => normalized.Length == 0 || key.StartsWith(normalized + "/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).ToArray()
            : [];
        return Task.FromResult(keys);
    }

    public Task DeleteAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(bucket, key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(
        string bucket,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        var directory = ResolvePath(bucket, prefix);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private void EnsurePublicBucket(string bucket)
    {
        if (!string.Equals(bucket, options.Value.PublicBucket, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Public URLs are only available for the public bucket.");
        }
    }

    private string ResolvePath(string bucket, string key)
    {
        var root = Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "storage", bucket);
        var safeKey = key.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(root, safeKey));
    }
}
