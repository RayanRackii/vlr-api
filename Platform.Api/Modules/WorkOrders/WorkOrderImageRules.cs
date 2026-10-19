namespace Platform.Api.Modules.WorkOrders;

public static class WorkOrderImageRules
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public const long RequestBytes = MaxBytes + (1024 * 1024);

    public const string TooLarge = "FILE_TOO_LARGE";

    public const string TypeNotAllowed = "FILE_TYPE_NOT_ALLOWED";

    public const string ContentMismatch = "FILE_CONTENT_MISMATCH";

    private static readonly string[] AllowedMediaTypes = ["image/jpeg", "image/png", "image/webp"];

    public static string Validate(string? contentType, long sizeBytes, ReadOnlySpan<byte> header)
    {
        if (sizeBytes <= 0 || sizeBytes > MaxBytes)
        {
            throw new WorkOrderImageValidationException(
                TooLarge,
                "Work order images must be between 1 byte and 5 MB.");
        }

        var mediaType = CanonicalMediaType(contentType);
        if (mediaType is null || !AllowedMediaTypes.Contains(mediaType, StringComparer.Ordinal))
        {
            throw new WorkOrderImageValidationException(
                TypeNotAllowed,
                "Work order images must be jpeg, png, or webp.");
        }

        if (!MatchesMagic(mediaType, header))
        {
            throw new WorkOrderImageValidationException(
                ContentMismatch,
                "File content does not match the declared image type.");
        }

        return mediaType;
    }

    public static string StorageKey(Guid tenantId, Guid workOrderId, Guid taskId, Guid objectId) =>
        $"{tenantId:N}/work-orders/{workOrderId:N}/tasks/{taskId:N}/{objectId:N}";

    public static bool IsKeyForTask(string storageKey, Guid tenantId, Guid workOrderId, Guid taskId)
    {
        var prefix = $"{tenantId:N}/work-orders/{workOrderId:N}/tasks/{taskId:N}/";
        if (!storageKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = storageKey[prefix.Length..];
        return Guid.TryParseExact(suffix, "N", out _);
    }

    public static string SanitizeFileName(string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "image" : Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            name = "image";
        }

        return name.Length <= 260 ? name : name[..260];
    }

    private static string? CanonicalMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mediaType.Length == 0 ? null : mediaType;
    }

    private static bool MatchesMagic(string mediaType, ReadOnlySpan<byte> header)
    {
        if (mediaType.Equals("image/png", StringComparison.Ordinal))
        {
            return header.Length >= 4
                && header[0] == 0x89
                && header[1] == 0x50
                && header[2] == 0x4E
                && header[3] == 0x47;
        }

        if (mediaType.Equals("image/jpeg", StringComparison.Ordinal))
        {
            return header.Length >= 3
                && header[0] == 0xFF
                && header[1] == 0xD8
                && header[2] == 0xFF;
        }

        return header.Length >= 12
            && header[0] == (byte)'R'
            && header[1] == (byte)'I'
            && header[2] == (byte)'F'
            && header[3] == (byte)'F'
            && header[8] == (byte)'W'
            && header[9] == (byte)'E'
            && header[10] == (byte)'B'
            && header[11] == (byte)'P';
    }
}

public sealed class WorkOrderImageValidationException : ArgumentException
{
    public WorkOrderImageValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
