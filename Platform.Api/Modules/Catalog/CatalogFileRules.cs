using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog;

public static class CatalogFileRules
{
    public const long CustomerVisibleMaxBytes = 5 * 1024 * 1024;
    public const long InternalB2BMaxBytes = 25 * 1024 * 1024;

    private static readonly HashSet<string> CustomerVisibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp",
    };

    private static readonly HashSet<string> CustomerVisibleMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp",
    };

    private static readonly HashSet<string> InternalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".stl", ".step", ".stp", ".dxf",
    };

    private static readonly HashSet<string> InternalMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/sla",
        "model/stl",
        "application/vnd.ms-pki.stl",
        "application/step",
        "model/step",
        "application/dxf",
        "image/vnd.dxf",
        "application/octet-stream",
    };

    public static void Validate(
        CatalogFileVisibility visibility,
        string fileName,
        string contentType,
        long sizeBytes,
        ReadOnlySpan<byte> header)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ArgumentException("File extension is required.");
        }

        if (visibility == CatalogFileVisibility.CustomerVisible)
        {
            if (sizeBytes <= 0 || sizeBytes > CustomerVisibleMaxBytes)
            {
                throw new ArgumentException("Customer-visible images must be between 1 byte and 5 MB.");
            }

            if (!CustomerVisibleExtensions.Contains(extension) || !CustomerVisibleMimeTypes.Contains(contentType))
            {
                throw new ArgumentException("Customer-visible files must be jpeg, png, or webp.");
            }

            EnsureImageMagic(extension, header);
            return;
        }

        if (sizeBytes <= 0 || sizeBytes > InternalB2BMaxBytes)
        {
            throw new ArgumentException("Internal files must be between 1 byte and 25 MB.");
        }

        if (!InternalExtensions.Contains(extension) || !InternalMimeTypes.Contains(contentType))
        {
            throw new ArgumentException("Internal files must be pdf, stl, step, stp, or dxf.");
        }

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            EnsurePdfMagic(header);
        }
    }

    private static void EnsureImageMagic(string extension, ReadOnlySpan<byte> header)
    {
        if (header.Length < 12)
        {
            throw new ArgumentException("File content does not match the declared image type.");
        }

        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
            {
                throw new ArgumentException("File content does not match the declared image type.");
            }

            return;
        }

        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            if (header[0] != 0xFF || header[1] != 0xD8 || header[2] != 0xFF)
            {
                throw new ArgumentException("File content does not match the declared image type.");
            }

            return;
        }

        // webp: RIFF....WEBP
        if (header[0] != (byte)'R' || header[1] != (byte)'I' || header[2] != (byte)'F' || header[3] != (byte)'F'
            || header[8] != (byte)'W' || header[9] != (byte)'E' || header[10] != (byte)'B' || header[11] != (byte)'P')
        {
            throw new ArgumentException("File content does not match the declared image type.");
        }
    }

    private static void EnsurePdfMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4
            || header[0] != (byte)'%'
            || header[1] != (byte)'P'
            || header[2] != (byte)'D'
            || header[3] != (byte)'F')
        {
            throw new ArgumentException("File content does not match the declared PDF type.");
        }
    }

    public static void ValidateRequestUpload(
        string fileName,
        string contentType,
        long sizeBytes,
        ReadOnlySpan<byte> header)
    {
        try
        {
            Validate(CatalogFileVisibility.CustomerVisible, fileName, contentType, sizeBytes, header);
        }
        catch (ArgumentException)
        {
            Validate(CatalogFileVisibility.InternalB2B, fileName, contentType, sizeBytes, header);
        }
    }

    public static string StorageKey(Guid tenantId, Guid ownerId, Guid fileId) =>
        $"{tenantId:N}/{ownerId:N}/{fileId:N}";
}
