namespace Platform.Api.Storage;

public enum StorageProviderErrorKind
{
    Conflict,
    Client,
    Upstream,
}

public sealed class StorageProviderException : Exception
{
    public const string UpstreamMessage = "Storage provider rejected the request.";
    public const string ClientMessage = "The file was rejected by storage.";
    public const string ConflictMessage = "The resource already exists.";
    public const string NotConfiguredMessage = "Storage is not configured correctly.";

    public StorageProviderErrorKind Kind { get; }

    public StorageProviderException(StorageProviderErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }
}
