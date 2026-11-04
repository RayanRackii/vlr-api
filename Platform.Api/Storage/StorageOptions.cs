namespace Platform.Api.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    // Explicitly preserve HttpClient's default request timeout (100 seconds).
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

    public string PublicBucket { get; set; } = "catalog-public";

    public string PrivateBucket { get; set; } = "catalog-private";

    public int SignedUrlTtlSeconds { get; set; } = 900;
}
