namespace Platform.Api.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    // Explicitly preserve HttpClient's default request timeout (100 seconds).
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

    // 100-second storage request timeout + 80-second late-completion safety margin = 3 minutes.
    public static readonly TimeSpan PendingFileRemovalMinAge = RequestTimeout + TimeSpan.FromSeconds(80);

    public string PublicBucket { get; set; } = "catalog-public";

    public string PrivateBucket { get; set; } = "catalog-private";

    public int SignedUrlTtlSeconds { get; set; } = 900;
}
