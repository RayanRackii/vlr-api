namespace Platform.Api.Modules.Company.Services;

/// <summary>Upload rules for the customer portal landing image: jpg/png/webp, magic-byte checked, at most 3 MB.</summary>
public static class PortalLandingImageRules
{
    public const long MaxBytes = 3 * 1024 * 1024;

    public const string TooLarge = "FILE_TOO_LARGE";

    public const string TypeNotAllowed = "FILE_TYPE_NOT_ALLOWED";

    public const string ContentMismatch = "FILE_CONTENT_MISMATCH";

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp",
    };

    private static readonly HashSet<string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp",
    };

    /// <summary>Validates the upload and returns the normalized extension (".jpg", ".png" or ".webp").</summary>
    public static string Validate(string fileName, string contentType, long sizeBytes, ReadOnlySpan<byte> header)
    {
        if (sizeBytes <= 0 || sizeBytes > MaxBytes)
        {
            throw new PortalLandingImageException(TooLarge, "The image must be between 1 byte and 3 MB.");
        }

        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension)
            || !Extensions.Contains(extension)
            || !MimeTypes.Contains(contentType ?? string.Empty))
        {
            throw new PortalLandingImageException(TypeNotAllowed, "The image must be jpeg, png or webp.");
        }

        if (header.Length < 12)
        {
            throw Mismatch();
        }

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
            {
                throw Mismatch();
            }

            return ".png";
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (header[0] != 0xFF || header[1] != 0xD8 || header[2] != 0xFF)
            {
                throw Mismatch();
            }

            return ".jpg";
        }

        // webp: RIFF....WEBP
        if (header[0] != (byte)'R' || header[1] != (byte)'I' || header[2] != (byte)'F' || header[3] != (byte)'F'
            || header[8] != (byte)'W' || header[9] != (byte)'E' || header[10] != (byte)'B' || header[11] != (byte)'P')
        {
            throw Mismatch();
        }

        return ".webp";
    }

    public static string StorageKey(Guid tenantId, Guid fileId, string extension) =>
        $"tenants/{tenantId:N}/landing/{fileId:N}{extension}";

    private static PortalLandingImageException Mismatch() =>
        new(ContentMismatch, "File content does not match the declared image type.");
}

public sealed class PortalLandingImageException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}
