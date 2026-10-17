using Platform.Api.Modules.Catalog;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogFileRulesTests
{
    [Fact]
    public void Customer_visible_requires_image_magic_and_extension()
    {
        var png = new byte[16];
        png[0] = 0x89;
        png[1] = (byte)'P';
        png[2] = (byte)'N';
        png[3] = (byte)'G';
        CatalogFileRules.Validate(CatalogFileVisibility.CustomerVisible, "a.png", "image/png", png.Length, png);

        var mismatch = Assert.Throws<CatalogFileValidationException>(
            () => CatalogFileRules.Validate(
                CatalogFileVisibility.CustomerVisible,
                "a.png",
                "image/png",
                png.Length,
                "not-a-png-file!!"u8));
        Assert.Equal(CatalogFileValidationException.ContentMismatch, mismatch.Code);
        var wrongType = Assert.Throws<CatalogFileValidationException>(
            () => CatalogFileRules.Validate(
                CatalogFileVisibility.CustomerVisible,
                "a.pdf",
                "application/pdf",
                4,
                "%PDF"u8));
        Assert.Equal(CatalogFileValidationException.TypeNotAllowed, wrongType.Code);
    }

    [Fact]
    public void Internal_files_never_use_public_image_rules()
    {
        CatalogFileRules.Validate(
            CatalogFileVisibility.InternalB2B,
            "part.stl",
            "model/stl",
            128,
            new byte[128]);
        CatalogFileRules.Validate(
            CatalogFileVisibility.InternalB2B,
            "draw.dxf",
            "application/dxf",
            64,
            new byte[64]);
    }

    [Fact]
    public void Six_megabyte_png_is_too_large_for_customer_visible_upload()
    {
        var header = PngHeader();
        const long sixMegabytes = 6L * 1024 * 1024;

        var customer = Assert.Throws<CatalogFileValidationException>(() =>
            CatalogFileRules.Validate(
                CatalogFileVisibility.CustomerVisible,
                "photo.png",
                "image/png",
                sixMegabytes,
                header));
        var upload = Assert.Throws<CatalogFileValidationException>(() =>
            CatalogFileRules.ValidateRequestUpload(
                "photo.png",
                "image/png",
                sixMegabytes,
                header));

        Assert.Equal(CatalogFileValidationException.TooLarge, customer.Code);
        Assert.Equal("FILE_TOO_LARGE", upload.Code);
        Assert.Contains("5 MB", upload.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Customer_image_with_wrong_magic_returns_content_mismatch()
    {
        var mismatch = Assert.Throws<CatalogFileValidationException>(() =>
            CatalogFileRules.ValidateRequestUpload(
                "photo.png",
                "image/png",
                32,
                "not-a-png-file!!"u8));

        Assert.Equal(CatalogFileValidationException.ContentMismatch, mismatch.Code);
        Assert.Equal("FILE_CONTENT_MISMATCH", mismatch.Code);
    }

    [Fact]
    public void Unknown_upload_type_returns_type_not_allowed()
    {
        var rejected = Assert.Throws<CatalogFileValidationException>(() =>
            CatalogFileRules.ValidateRequestUpload(
                "notes.txt",
                "text/plain",
                16,
                "hello"u8));

        Assert.Equal(CatalogFileValidationException.TypeNotAllowed, rejected.Code);
    }

    private static byte[] PngHeader()
    {
        var png = new byte[16];
        png[0] = 0x89;
        png[1] = (byte)'P';
        png[2] = (byte)'N';
        png[3] = (byte)'G';
        return png;
    }
}
