namespace Platform.Api.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string PublicBucket { get; set; } = "catalog-public";

    public string PrivateBucket { get; set; } = "catalog-private";

    public int SignedUrlTtlSeconds { get; set; } = 900;

    public string? SupabaseUrl { get; set; }

    public string? ServiceRoleKey { get; set; }
}
